using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Mod.Interfaces;
using GenericJobs.Configuration;
using GenericJobs.Template;
using System.Diagnostics;
using Reloaded.Memory.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using System.Runtime.InteropServices;
using Reloaded.Memory.Sigscan;
using System;
using Reloaded.Memory.Sigscan.Definitions.Structs;

namespace GenericJobs
{
    /// <summary>
    /// Job selection mod that adds vertical paging for hidden jobs (Dark Knight)
    /// </summary>
    public class Mod : ModBase
    {
        private readonly IModLoader _modLoader;
        private readonly IReloadedHooks? _hooks;
        private readonly ILogger _logger;
        private readonly IMod _owner;
        private Config _configuration;
        private readonly IModConfig _modConfig;
        private static IStartupScanner? _startupScanner = null!;
        private static int MaxJobsPerPage = 19;
        private static int MaxJobs = 21;

        // Game base address
        private nuint _gameBase;

        // Cached extra jobs
        private ushort[] _extraJobs = Array.Empty<ushort>();

        // State tracking
        private ushort[] _originalJobList = new ushort[MaxJobs]; // Store original jobs
        private int _pageOffset = 0; // 0 = page 1, 1 = page 2
        private bool _dontReset = true;
        private Dictionary<int, string> _jobNameLookup = new();
        private nint _jobMenuUI = 0;
        private byte[] _jobMenuUIData = new byte[0xB00];
        private byte[] _jobMenuPageOneData = new byte[0xB00];
        private int _jobMenuState = 0;

        // Hook objects
        private IHook<Sub363718Delegate>? _sub363718Hook;
        private IHook<Sub12FBB8Delegate>? _sub12FBB8Hook;
        private IHook<UpdateJobListDelegate>? _updateJobListHook;
        private IHook<UpdateLevelRequirementsPopupDelegate>? _updateLevelRequirementsPopup;
        private IHook<SetJobRequirementNameDelegate>? _setJobRequirementName;
        private IHook<HandleJobMenuClickDelegate>? _handleJobMenuClick;
        private IHook<HandleJobMenuStateDelegate>? _handleJobMenuState;

        // Patched addresses
        private nuint _jobPatch1 = 0;
        private nuint _jobPatch2 = 0;
        private nuint _jobPatch3 = 0;
        private nuint _jobPatch4 = 0;
        private nuint _jobPatch5 = 0;

        // Offsets
        private long _jobList = 0;

        // Function delegates
        [Function(CallingConventions.Microsoft)]
        private delegate byte Sub363718Delegate();

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub12FBB8Delegate(nint a1, nint a2, int a3, int a4);

        [Function(CallingConventions.Microsoft)]
        private delegate nint UpdateJobListDelegate(nint jobList, int a2, nint a3);

        [Function(CallingConventions.Microsoft)]
        private delegate nint Sub3CEE14Delegate(nint a1, uint a2);

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub1309C0Delegate(nint a1, int a2, byte a3);

        [Function(CallingConventions.Microsoft)]
        private delegate byte SubF5E3CDelegate(nint a1, nint a2, int a3, int a4);

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub131A6CDelegate(nint a1);

        [Function(CallingConventions.Microsoft)]
        private delegate char UpdateLevelRequirementsPopupDelegate(nint a1);

        [Function(CallingConventions.Microsoft)]
        private delegate void SetJobRequirementNameDelegate(nint a1, nint a2);

        [Function(CallingConventions.Microsoft)]
        private delegate void HandleJobMenuClickDelegate(nint a1, int a2, nint a3, nint a4);

        [Function(CallingConventions.Microsoft)]
        private delegate void HandleJobMenuStateDelegate(nint a1, nint a2, uint a3, short a4, int a5);

        // Wrapper for original functions
        private Sub1309C0Delegate? _sub1309C0;
        private SubF5E3CDelegate? _subF5E3C;

        public Mod(ModContext context)
        {
            _modLoader = context.ModLoader;
            _hooks = context.Hooks;
            _logger = context.Logger;
            _owner = context.Owner;
            _configuration = context.Configuration;
            _modConfig = context.ModConfig;

#if DEBUG
            Debugger.Launch();
#endif
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Initializing...");

                var process = Process.GetCurrentProcess();
                var mainModule = process.MainModule;
                if (mainModule == null)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Failed to get main module!");
                    return;
                }

                _gameBase = (nuint)mainModule.BaseAddress;
                _logger.WriteLine($"[{_modConfig.ModId}] Game base: 0x{_gameBase:X}");

                if (!mainModule.ModuleName.Equals("FFT_enhanced.exe", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Wrong executable: {mainModule.ModuleName}");
                    return;
                }

                _extraJobs = ParseExtraJobs();
                _logger.WriteLine($"[{_modConfig.ModId}] Loaded {_extraJobs.Length} extra jobs");

                var scanner = new Scanner(process, process.MainModule);

                InstallHooks(scanner);
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Error during initialization: {ex}");
            }
        }

        void InstallHooks(Scanner scanner)
        {
            if (_hooks == null)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Hooks interface is null!");
                return;
            }

            var startupScannerController = _modLoader.GetController<IStartupScanner>();
            if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Could not fetch IStartupScanner", _logger.ColorRed);
                return;
            }

            var patterns = new Dictionary<string, (string pattern, Action<PatternScanResult> handler)>
            {
                ["Sub1309C0"] = (
                    "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 80 B9 ?? ?? ?? ?? ?? 48 8B D9 4C 63 CA",
                    e => _sub1309C0 = _hooks.CreateWrapper<Sub1309C0Delegate>((long)_gameBase + e.Offset, out _)
                ),
                ["SubF5E3C"] = (
                    "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 41 8B D9 41 8B E8",
                    e => _subF5E3C = _hooks.CreateWrapper<SubF5E3CDelegate>((long)_gameBase + e.Offset, out _)
                ),
                ["Sub363718Hook"] = (
                    "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 54 41 55 41 56 41 57 48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 4C 8D 25",
                    e => _sub363718Hook = _hooks.CreateHook<Sub363718Delegate>(Sub363718Hook, (long)_gameBase + e.Offset).Activate()
                ),
                ["Sub12FBB8Hook"] = (
                    "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? 41 54 41 56 41 57 48 83 EC ?? 45 8B F9",
                    e => _sub12FBB8Hook = _hooks.CreateHook<Sub12FBB8Delegate>(Sub12FBB8Hook, (long)_gameBase + e.Offset).Activate()
                ),
                ["UpdateJobListHook"] = (
                    "48 89 E0 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 48 ?? 57 41 54",
                    e => _updateJobListHook = _hooks.CreateHook<UpdateJobListDelegate>(UpdateJobListHook, (long)_gameBase + e.Offset).Activate()
                ),
                ["UpdateLevelRequirementsPopup"] = (
                    "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 54 41 55 41 56 41 57 48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 63 81",
                    e => _updateLevelRequirementsPopup = _hooks.CreateHook<UpdateLevelRequirementsPopupDelegate>(UpdateLevelRequirementsPopupHook, (long)_gameBase + e.Offset).Activate()
                ),
                ["SetJobRequirementName"] = (
                    "40 53 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 48 8B D9 48 89 81 ?? ?? ?? ?? 48 81 C1",
                    e => _setJobRequirementName = _hooks.CreateHook<SetJobRequirementNameDelegate>(SetJobRequirementNameHook, (long)_gameBase + e.Offset).Activate()
                ),
                ["HandleJobMenuClick"] = (
                    "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 44 8B B9",
                    e => _handleJobMenuClick = _hooks.CreateHook<HandleJobMenuClickDelegate>(HandleJobMenuClickHook, (long)_gameBase + e.Offset).Activate()
                ),
                ["HandleJobMenuState"] = (
                    "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? 41 56 48 83 EC ?? 48 8B 01 41 8B E9",
                    e => _handleJobMenuState = _hooks.CreateHook<HandleJobMenuStateDelegate>(HandleJobMenuStateHook, (long)_gameBase + e.Offset).Activate()
                ),
                ["JobPatch1"] = (
                    "89 83 ?? ?? ?? ?? 45 33 C0 8B D7",
                    e => _jobPatch1 = _gameBase + (nuint)e.Offset
                ),
                ["JobPatch2"] = (
                    "66 89 0E EB ?? 0F B7 CB",
                    e => _jobPatch2 = _gameBase + (nuint)e.Offset
                ),
                ["JobPatch3"] = (
                    "83 8F ?? ?? ?? ?? ?? 4C 8D 5C 24",
                    e => _jobPatch3 = _gameBase + (nuint)e.Offset
                ),
                ["JobPatch4"] = (
                    "66 89 1E 44 01 ED",
                    e => _jobPatch4 = _gameBase + (nuint)e.Offset
                ),
                ["JobPatch5"] = (
                    "75 ?? 41 09 C9 66 45 89 0F",
                    e => _jobPatch5 = _gameBase + (nuint)e.Offset + 5
                ),
                ["JobFlagsPatch1"] = (
                    "48 8B 6C 24 ?? 89 D8 48 8B 5C 24 ?? 48 8B 74 24 ?? 48 83 C4 ?? 41 5F 41 5E 41 5D",
                    e => WriteMemory(_gameBase + (nuint)e.Offset - 3, [0x90, 0x90, 0x90])
                ),
                ["JobFlagsPatch2"] = (
                    "89 F0 89 CF",
                    e => WriteMemory(_gameBase + (nuint)(e.Offset - 7), [0x41, 0xBF, 0x15, 0x00, 0x00, 0x00, 0x90])
                )
            };

            var darkKnightPatterns = new Dictionary<string, (string pattern, Action<PatternScanResult> handler)>
            {
                ["DK_SanguineSword1"] = (
                    "03 66 00 01 74",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x07, 0x00, 0x00])
                ),
                ["DK_SanguineSword2"] = (
                    "A1 00 20 01 21 01",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0xAD, 0x00])
                ),
                ["DK_SanguineSword3"] = (
                    "F4 01 32 41 82 00 02 A0",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0xF4, 0x01, 0x32, 0x41, 0x82, 0x00, 0x02, 0xA0])
                ),
                ["DK_SanguineSword4"] = (
                    "01 00 03 09 10 10 00 00 3C 00 00 00 00 00 00 00 FF FF FF FF 04",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x03, 0x00, 0x00, 0x05, 0x11, 0x12, 0x08, 0x01, 0x65, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
                ),
                ["DK_InfernalStrike"] = (
                    "C0 00 C1 00 C2 00 C3 00",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0xAC, 0x00])
                ),
                ["DK_CrushingBlow"] = (
                    "E7 00 E8 00 E9 00",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0xF6, 0x00])
                ),
                ["DK_AbyssalBlade"] = (
                    "E8 00 E9 00 EA 00",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x62, 0x00])
                )
            };

            if (_extraJobs.Contains((ushort)0xA0))
            {
                patterns = patterns.Concat(darkKnightPatterns).ToDictionary(x => x.Key, x => x.Value);
            }

            _logger.WriteLine($"[{_modConfig.ModId}] Scanning for {patterns.Count} patterns...");

            foreach (var kvp in patterns)
            {
                var name = kvp.Key;
                var (pattern, handler) = kvp.Value;

                _startupScanner.AddMainModuleScan(pattern, result =>
                {
                    if (result.Found)
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] {name,-30} -> 0x{(_gameBase + (nuint)result.Offset):X}");
                        handler(result);
                    }
                    else
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] {name,-30} -> NOT FOUND", _logger.ColorRed);
                    }
                });
            }
        }

        private unsafe void WriteMemory(nuint address, byte[] data)
        {
            fixed (byte* dataPtr = data)
            {
                Reloaded.Memory.Memory.Instance.SafeWrite(address, new Span<byte>(dataPtr, data.Length));
            }
        }

        private byte Sub363718Hook()
        {
            if (_pageOffset > 0 && (_jobMenuState == 1402 || _jobMenuState == 1404))
                return 0;

            byte result = _sub363718Hook!.OriginalFunction();

            if (!_dontReset)
            {
                _pageOffset = 0;
                Array.Clear(_originalJobList, 0, _originalJobList.Length);
            }

            return result;
        }

        private ushort[] ParseExtraJobs()
        {
            var tempList = "A0, A1";

            if (string.IsNullOrWhiteSpace(tempList))
                return Array.Empty<ushort>();

            try
            {
                return tempList
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s =>
                    {
                        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            return Convert.ToUInt16(s[2..], 16);

                        if (s.Any(c => "ABCDEFabcdef".Contains(c)))
                            return Convert.ToUInt16(s, 16);

                        return Convert.ToUInt16(s, 10);
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Error parsing ExtraJobsHex ({ex.Message}), using defaults");
                return new ushort[] { 0xA0 };
            }
        }

        private unsafe void UpdateJobListDisplay()
        {
            ushort* joblist = (ushort*)(_jobList);

            if (_pageOffset == 0)
            {
                for (int i = 0; i < MaxJobs; i++)
                {
                    joblist[i] = _originalJobList[i];
                }
            }
            else
            {
                for (int i = 0; i < 6; i++)
                {
                    joblist[i] = _originalJobList[13 + i];
                }

                for (int i = 6; i < MaxJobsPerPage; i++)
                {
                    int extraIndex = i - 6;
                    if (extraIndex < _extraJobs.Length)
                    {
                        joblist[i] = _extraJobs[extraIndex];

                        // temp fix until actual generics
                        if (joblist[i] == 0xa0)
                        {
                            joblist[i] += (ushort)(_originalJobList[19] - 0xa0);
                        }

                        if (joblist[i] == 0xa1)
                        {
                            joblist[i] += (ushort)(_originalJobList[20] - 0xa1);
                        }
                    }
                    else
                    {
                        joblist[i] = 0xFFFF; // End of list
                    }
                }
            }
        }

        private unsafe void ApplyTempPatches(bool disable)
        {
            if (disable)
            {
                WriteMemory(_jobPatch1, [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);
                WriteMemory(_jobPatch2, [0x90, 0x90, 0x90]);
                WriteMemory(_jobPatch3, [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);
                WriteMemory(_jobPatch4, [0x90, 0x90, 0x90]);
                WriteMemory(_jobPatch5, [0x90, 0x90, 0x90, 0x90]);
            }
            else
            {
                WriteMemory(_jobPatch1, [0x89, 0x83, 0x38, 0x4A, 0x00, 0x00]);
                WriteMemory(_jobPatch2, [0x66, 0x89, 0x0E]);
                WriteMemory(_jobPatch3, [0x83, 0x8F, 0x38, 0x4A, 0x00, 0x00, 0xFF]);
                WriteMemory(_jobPatch4, [0x66, 0x89, 0x1E]);
                WriteMemory(_jobPatch5, [0x66, 0x45, 0x89, 0x0F]);
            }
        }

        private unsafe void Sub12FBB8Hook(nint a1, nint a2, int a3, int a4)
        {
            int currentIndex = *(int*)(a1 + 0x4A38);
            ushort* joblist = (ushort*)(_jobList);

            if (_originalJobList[0] == 0)
            {
                for (int i = 0; i < MaxJobs; i++)
                    _originalJobList[i] = joblist[i];
            }

            _sub12FBB8Hook!.OriginalFunction(a1, a2, a3, a4);

            int newIndex = *(int*)(a1 + 0x4A38);

            // Detect scrolling down: was on bottom row (13-18), now wrapped to first row (0-5)
            if (currentIndex >= 13 && currentIndex <= 18 && newIndex >= 0 && newIndex < 6)
            {
                if (_pageOffset == 0 && _extraJobs.Length > 0)
                {
                    Marshal.Copy(IntPtr.Add(a1, 8816), _jobMenuPageOneData, 0, 0xB00);

                    // Go to page 2
                    ApplyTempPatches(true);
                    _pageOffset = 1;
                    //_logger.WriteLine($"[{_modConfig.ModId}] PAGE DOWN - Scrolling to page 2 (was {currentIndex}, would be {newIndex})");

                    UpdateJobListDisplay();

                    *(int*)(a1 + 0x4A38) = newIndex;

                    _dontReset = true;
                    _sub363718Hook!.OriginalFunction();
                    _dontReset = false;

                    _sub1309C0!(a1, newIndex, 0);
                    *(int*)(a1 + 0x4A38) = newIndex;
                    *(int*)(a2 + 4) = newIndex;
                    *(byte*)(a2 + 8) = 1;

                    ApplyTempPatches(false);
                    return;
                }
                else if (_pageOffset == 1)
                {
                    // Wrap back to page 1
                    ApplyTempPatches(true);
                    _pageOffset = 0;
                    //_logger.WriteLine($"[{_modConfig.ModId}] PAGE WRAP - Scrolling back to page 1 (was {currentIndex}, would be {newIndex})");

                    UpdateJobListDisplay();

                    *(int*)(a1 + 0x4A38) = newIndex;

                    _dontReset = true;
                    _sub363718Hook!.OriginalFunction();
                    _dontReset = false;

                    _sub1309C0!(a1, newIndex, 0);
                    *(int*)(a1 + 0x4A38) = newIndex;
                    *(int*)(a2 + 4) = newIndex;
                    *(byte*)(a2 + 8) = 1;

                    ApplyTempPatches(false);
                    return;
                }
            }

            // Detect scrolling up: was on top row (0-5), now wrapped to bottom rows (13-18)
            if (currentIndex >= 0 && currentIndex <= 5 && newIndex >= 13 && newIndex <= 18)
            {
                if (_pageOffset == 1)
                {
                    // Go back to page 1
                    ApplyTempPatches(true);
                    _pageOffset = 0;
                    UpdateJobListDisplay();

                    *(int*)(a1 + 0x4A38) = newIndex;

                    _dontReset = true;
                    _sub363718Hook!.OriginalFunction();
                    _dontReset = false;

                    _sub1309C0!(a1, newIndex, 0);
                    *(int*)(a1 + 0x4A38) = newIndex;
                    *(int*)(a2 + 4) = newIndex;
                    *(byte*)(a2 + 8) = 1;

                    ApplyTempPatches(false);
                    return;
                }
                else if (_pageOffset == 0 && _extraJobs.Length > 0)
                {
                    Marshal.Copy(IntPtr.Add(a1, 8816), _jobMenuPageOneData, 0, 0xB00);

                    // Wrap to page 2 (last page)
                    ApplyTempPatches(true);
                    _pageOffset = 1;
                    _logger.WriteLine($"[{_modConfig.ModId}] PAGE WRAP UP - Scrolling to page 2 (was {currentIndex}, would be {newIndex})");

                    UpdateJobListDisplay();

                    *(int*)(a1 + 0x4A38) = newIndex;

                    _dontReset = true;
                    _sub363718Hook!.OriginalFunction();
                    _dontReset = false;

                    _sub1309C0!(a1, newIndex, 0);
                    *(int*)(a1 + 0x4A38) = newIndex;
                    *(int*)(a2 + 4) = newIndex;
                    *(byte*)(a2 + 8) = 1;

                    ApplyTempPatches(false);
                    return;
                }
            }
        }

        private nint UpdateJobListHook(nint jobList, int a2, nint a3)
        {
            _jobList = jobList;
            return _updateJobListHook!.OriginalFunction(jobList, a2, a3);
        }

        private unsafe void HandleJobMenuClickHook(nint a1, int a2, nint a3, nint a4)
        {
            int selectedIndex = *(int*)(a1 + 19000);

            if (_pageOffset == 1 && selectedIndex > 5 + _extraJobs.Length)
                return;

            _handleJobMenuClick?.OriginalFunction(a1, a2, a3, a4);
        }

        private void HandleJobMenuStateHook(nint a1, nint a2, uint a3, short a4, int state)
        {
            var ui = Marshal.ReadIntPtr(IntPtr.Add(a1, 0x78));

            _jobMenuState = state;

            // temp fix, move back to page 1 when viewing job tree
            if (ui > 0 && _pageOffset > 0)
            {
                if (state == 1409)
                {
                    _pageOffset = 0;
                    UpdateJobListDisplay();
                    _sub363718Hook!.OriginalFunction();
                }
            }

            _handleJobMenuState?.OriginalFunction(a1, a2, a3, a4, state);
        }

        private unsafe char UpdateLevelRequirementsPopupHook(nint a1)
        {
            _jobMenuUI = a1;
            byte[] tempJobData = new byte[0xB00];
            Marshal.Copy(IntPtr.Add(a1, 8816), tempJobData, 0, 0xB00);
            var result = _updateLevelRequirementsPopup.OriginalFunction(a1);
            Marshal.Copy(tempJobData, 0, IntPtr.Add(a1, 8816), 0xB00);
            _jobMenuUI = 0;
            return result;
        }

        private void SetJobRequirementNameHook(nint a1, nint a2)
        {
            _setJobRequirementName?.OriginalFunction(a1, a2);

            if (_jobMenuUI != 0 && _pageOffset > 0)
            {
                Marshal.Copy(_jobMenuPageOneData, 0, IntPtr.Add(_jobMenuUI, 8816), 0xB00);
            }
        }

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            _configuration = configuration;
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");

            _extraJobs = ParseExtraJobs();
            _logger.WriteLine($"[{_modConfig.ModId}] Reloaded {_extraJobs.Length} extra jobs");
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}
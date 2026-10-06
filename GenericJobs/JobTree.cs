using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.Sigscan.Definitions.Structs;
using System.Runtime.InteropServices;

namespace GenericJobs
{
    /// <summary>
    /// Job tree support for Dark Knight and Onion Knight.
    ///
    /// The job tree UI object (allocated with a fixed 0x2FD8 bytes) holds exactly 20 jobs, Squire..Mime: per-job node
    /// entries at +0x48 (stride 0x1C8) and per-job slot data at +0x23E8 (stride 0x94), followed by its other fields at
    /// +0x2F78. The object is enlarged and the slot data moved to the new space at the end (+0x2FD8), which leaves room
    /// for 22 node entries without moving anything else; every 20-job bound in the tree code becomes 22. Tree index =
    /// job slot: Squire..Mime 0-19, Dark Knight 20, Onion Knight 21.
    ///
    /// The tree finds each job's node in ffto_job_tree.uib by name (a slot -> name switch, hooked here for the new
    /// slots), and its cursor neighbours and lines come from generaljob.nxd rows 0-21.
    /// </summary>
    public partial class Mod
    {
        private const int TreeJobCount = 22;
        private const int TreeDarkKnight = 20;
        private const int TreeOnionKnight = 21;
        private const int TreeHoverIndexOffset = 0x2FC8;
        private const int TreeCurrentIndexOffset = 0x2FCC;
        private const int TreeClickedOffset = 0x2FD1;
        private const int TreeJobDataJobOffset = 0x48;
        private const byte ConfirmButton = 0x1A;

        private static readonly nint DarkKnightNodeName = Marshal.StringToHGlobalAnsi("DarkKnight");
        private static readonly nint OnionKnightNodeName = Marshal.StringToHGlobalAnsi("OnionKnight");

        private IAsmHook? _treeNodeName;
        private IHook<PopulateJobTreeSlotDelegate>? _populateJobTreeSlot;
        private IHook<JobTreeInputDelegate>? _jobTreeInput;
        private IsButtonTriggeredDelegate? _isButtonTriggered;

        [Function(CallingConventions.Microsoft)]
        private delegate void PopulateJobTreeSlotDelegate(nint tree, nint jobData, int index);

        [Function(CallingConventions.Microsoft)]
        private delegate void JobTreeInputDelegate(nint tree, nint state);

        [Function(CallingConventions.Microsoft)]
        private delegate byte IsButtonTriggeredDelegate(int a1, int a2, int button, byte a4);

        // (offset from the function start, original bytes, patched bytes); originals are checked before writing
        private static readonly (int offset, byte[] original, byte[] patched)[] TreeAllocPatches =
        {
            (0x1, [0xD8, 0x2F, 0x00, 0x00], [0x90, 0x3C, 0x00, 0x00]),                         // new(0x2FD8) -> 0x3C90
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeDeletePatches =
        {
            (0x1A, [0xD8, 0x2F, 0x00, 0x00], [0x90, 0x3C, 0x00, 0x00]),                        // sized delete
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeCtorPatches =
        {
            (0x29, [0x14], [0x16]),                                                            // entry + slot loops
            (0x67, [0xE8, 0x23, 0x00, 0x00], [0xD8, 0x2F, 0x00, 0x00]),                        // slot data base
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeDtorPatches =
        {
            (0x49, [0x14], [0x16]),                                                            // entry loop
            (0x50, [0x08, 0x24, 0x00, 0x00], [0x98, 0x27, 0x00, 0x00]),                        // entries end + 0x20
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeInitPatches =
        {
            (0x350, [0x14], [0x16]),                                                           // node lookup loop
            (0x4C2, [0x13], [0x15]),                                                           // line target bound
            (0x77E, [0x14], [0x16]),                                                           // generaljob row loop
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeEnterPatches =
        {
            (0x53, [0x13], [0x15]),                                                            // current node bound
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeInputPatches =
        {
            (0x131, [0x13], [0x15]),                                                           // hovered node bound
            (0x13D, [0x14], [0x16]),                                                           // current node bound
            (0x214, [0x13], [0x15]),                                                           // neighbour bound
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreePopulatePatches =
        {
            (0x4C, [0x13], [0x15]),                                                            // index bound
            (0x70, [0xE8, 0x23, 0x00, 0x00], [0xD8, 0x2F, 0x00, 0x00]),                        // slot data base
            (0xEE, [0x44, 0x24, 0x00, 0x00], [0x34, 0x30, 0x00, 0x00]),                        // slot image id field
            (0xF4, [0x44, 0x24, 0x00, 0x00], [0x34, 0x30, 0x00, 0x00]),
            (0x10B, [0x44, 0x24, 0x00, 0x00], [0x34, 0x30, 0x00, 0x00]),
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeHighlightPatches =
        {
            (0x26, [0x13], [0x15]),                                                            // node bound
            (0x4A, [0x13], [0x15]),                                                            // line target bounds
            (0xB2, [0x13], [0x15]),
            (0x5B, [0x6C, 0x24, 0x00, 0x00], [0x5C, 0x30, 0x00, 0x00]),                        // slot data fields
            (0x65, [0x2C, 0x24, 0x00, 0x00], [0x1C, 0x30, 0x00, 0x00]),
            (0xBF, [0x6C, 0x24, 0x00, 0x00], [0x5C, 0x30, 0x00, 0x00]),
            (0xC9, [0x2C, 0x24, 0x00, 0x00], [0x1C, 0x30, 0x00, 0x00]),
        };

        private static readonly (int offset, byte[] original, byte[] patched)[] TreeUnhighlightPatches =
        {
            (0x2, [0x13], [0x15]),                                                             // node bound
            (0x42, [0x13], [0x15]),                                                            // line target bounds
            (0x91, [0x13], [0x15]),
            (0x53, [0x6C, 0x24, 0x00, 0x00], [0x5C, 0x30, 0x00, 0x00]),                        // slot data fields
            (0x5D, [0x2C, 0x24, 0x00, 0x00], [0x1C, 0x30, 0x00, 0x00]),
            (0x9E, [0x6C, 0x24, 0x00, 0x00], [0x5C, 0x30, 0x00, 0x00]),
            (0xA8, [0x2C, 0x24, 0x00, 0x00], [0x1C, 0x30, 0x00, 0x00]),
        };

        // the job list builder (Sub363718): also feeds the tree Dark Knight and Onion Knight
        private static readonly (int offset, byte[] original, byte[] patched)[] JobListBuilderTreePatches =
        {
            (0x2C6, [0x13], [0x15]),                                                           // jobs built: 19 -> 21
            (0x699, [0x13], [0x15]),                                                           // line target bound
            (0x69F, [0x13], [0x15]),                                                           // tree index bound
            (0x6B1, [0x6C, 0x24, 0x00, 0x00], [0x5C, 0x30, 0x00, 0x00]),                       // slot data fields
            (0x6BA, [0x2C, 0x24, 0x00, 0x00], [0x1C, 0x30, 0x00, 0x00]),
            (0x6F4, [0x14], [0x16]),                                                           // tree loop
        };

        private Dictionary<string, (string pattern, Action<PatternScanResult> handler)> JobTreePatterns()
        {
            return new()
            {
                ["JobTreeAlloc"] = (
                    "B9 D8 2F 00 00 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B C8 E8 ?? ?? ?? ??",
                    e => ApplyPatches("JobTreeAlloc", e.Offset, TreeAllocPatches)
                ),
                ["JobTreeDelete"] = (
                    "48 89 5C 24 08 57 48 83 EC 20 8B DA 48 8B F9 E8 ?? ?? ?? ?? F6 C3 01 74 ?? BA D8 2F 00 00",
                    e => ApplyPatches("JobTreeDelete", e.Offset, TreeDeletePatches)
                ),
                ["JobTreeCtor"] = (
                    "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 56 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? BD 14 00 00 00 48 89 03",
                    e => ApplyPatches("JobTreeCtor", e.Offset, TreeCtorPatches)
                ),
                ["JobTreeDtor"] = (
                    "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 48 8D 05 ?? ?? ?? ?? 48 89 41 30 48 8D 05 ?? ?? ?? ?? 48 89 41 40 48 81 C1 80 2F 00 00 48 8D 05 ?? ?? ?? ??",
                    e => ApplyPatches("JobTreeDtor", e.Offset, TreeDtorPatches)
                ),
                ["JobTreeInit"] = (
                    "48 8B C4 48 89 58 10 48 89 70 18 48 89 78 20 55 41 54 41 55 41 56 41 57 48 8D A8 48 FD FF FF",
                    e =>
                    {
                        ApplyPatches("JobTreeInit", e.Offset, TreeInitPatches);
                        HookLineLabelName(e.Offset);
                    }
                ),
                ["JobTreeEnter"] = (
                    "48 89 5C 24 08 57 48 83 EC 30 8B FA 48 8B D9 41 81 F8 81 05 00 00 75 ?? 33 D2 E8 ?? ?? ?? ??",
                    e => ApplyPatches("JobTreeEnter", e.Offset, TreeEnterPatches)
                ),
                ["JobTreeInput"] = (
                    "48 89 5C 24 08 57 48 83 EC 30 45 33 C9 48 8B FA 48 8B D9 33 D2 33 C9 45 8D 41 1A E8 ?? ?? ?? ??",
                    e =>
                    {
                        ApplyPatches("JobTreeInput", e.Offset, TreeInputPatches);
                        nint address = (nint)(_gameBase + (nuint)e.Offset);
                        // the function's first call (+0x1B) is the button query
                        nint isButtonTriggered = address + 0x20 + Marshal.ReadInt32(address + 0x1C);
                        _isButtonTriggered = _hooks!.CreateWrapper<IsButtonTriggeredDelegate>(isButtonTriggered, out _);
                        _jobTreeInput = _hooks.CreateHook<JobTreeInputDelegate>(JobTreeInputHook, address).Activate();
                    }
                ),
                ["PopulateJobTreeSlot"] = (
                    "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 41 54 41 55 41 56 41 57 48 8B EC 48 81 EC 80 00 00 00 45 33 ED 45 8B F0 48 8B F2",
                    e =>
                    {
                        ApplyPatches("PopulateJobTreeSlot", e.Offset, TreePopulatePatches);
                        _populateJobTreeSlot = _hooks!.CreateHook<PopulateJobTreeSlotDelegate>(PopulateJobTreeSlotHook, (long)_gameBase + e.Offset).Activate();
                    }
                ),
                ["JobTreeHighlight"] = (
                    "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 54 41 56 41 57 48 83 EC 20 4C 63 F2 48 8B F1 41 83 FE 13",
                    e => ApplyPatches("JobTreeHighlight", e.Offset, TreeHighlightPatches)
                ),
                ["JobTreeUnhighlight"] = (
                    "83 FA 13 0F 87 ?? ?? ?? ?? 48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20",
                    e => ApplyPatches("JobTreeUnhighlight", e.Offset, TreeUnhighlightPatches)
                ),
                ["JobVisual"] = (
                    "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B F9 41 8A F0 8B 0A 48 8B DA",
                    e => _jobVisual = _hooks!.CreateHook<JobVisualDelegate>(JobVisualHook, (long)_gameBase + e.Offset).Activate()
                ),
                ["JobTreeNodeName"] = (
                    "83 C2 4A 83 FA 54 0F 8F ?? ?? ?? ?? 0F 84 ?? ?? ?? ?? 83 FA 4F 7F ?? 74 ??",
                    e => HookTreeNodeName(e.Offset)
                ),
            };
        }

        // Per-line level labels. Tree init builds each requirement line's name ("Line<required><job>") in a buffer, finds
        // that line node, then overwrites the buffer with the label name for the line's generaljob position type
        // (1 TextBoardJobLvL, 2 LvR, 3 and 5/6 LvB, 4 LvT) and finds the label inside the required job's node. Those
        // four boxes give a job one label per side. Types 7 and up skip the overwrite, so the buffer still holds the
        // line name; this renames it "LvLn<required><job>", a label node of its own in the UnitJob component, placed for
        // that line. Hook point: right after the label name switch (JobTreeInit+0x51A), buffer at rbp+0x170.
        private const int LineLabelHookOffset = 0x51A;
        private static readonly byte[] LineLabelHookBytes = [0x4C, 0x89, 0x75, 0x90, 0x4D, 0x69, 0xF6, 0xC8, 0x01, 0x00, 0x00];
        private IAsmHook? _lineLabelName;

        private unsafe void HookLineLabelName(int initOffset)
        {
            nuint address = _gameBase + (nuint)(initOffset + LineLabelHookOffset);
            if (!new ReadOnlySpan<byte>((void*)address, LineLabelHookBytes.Length).SequenceEqual(LineLabelHookBytes))
            {
                _logger.WriteLine($"[{_modConfig.ModId}] JobTreeInit: unexpected bytes at the line label hook, per-line labels disabled", _logger.ColorRed);
                return;
            }

            _lineLabelName = _hooks!.CreateAsmHook(new[]
            {
                "use64",
                "cmp dword [rbp+0x170], 0x656E694C",    // "Line"
                "jne done",
                "mov dword [rbp+0x170], 0x6E4C764C",    // "LvLn"
                "done:",
            }, (long)address, AsmHookBehaviour.ExecuteFirst).Activate();
        }

        private unsafe void ApplyPatches(string name, int functionOffset, (int offset, byte[] original, byte[] patched)[] patches)
        {
            nuint function = _gameBase + (nuint)functionOffset;
            foreach (var (offset, original, _) in patches)
            {
                var current = new ReadOnlySpan<byte>((void*)(function + (nuint)offset), original.Length);
                if (!current.SequenceEqual(original))
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] {name}: unexpected bytes at +0x{offset:X}, job tree patches skipped", _logger.ColorRed);
                    return;
                }
            }

            foreach (var (offset, _, patched) in patches)
                WriteMemory(function + (nuint)offset, patched);
        }

        // Job art (JobType visual icon) for a job: writes { int iconId; bool hidden } (0x1400FBEB8). From the job menu
        // (fromMenu != 0) "hidden" is set for every job outside Squire..Mime, and the menu's art setter (0x14029BCD4,
        // used by the Job Help screen) then shows nothing; Dark Knight and Onion Knight have art.
        [Function(CallingConventions.Microsoft)]
        private delegate void JobVisualDelegate(nint visual, nint job, byte fromMenu);
        private IHook<JobVisualDelegate>? _jobVisual;

        private unsafe void JobVisualHook(nint visual, nint job, byte fromMenu)
        {
            _jobVisual!.OriginalFunction(visual, job, fromMenu);
            if (fromMenu != 0 && (*(int*)job == 0xA0 || *(int*)job == 0xA1))
                *(byte*)(visual + 4) = 0;
        }

        // The slot -> node name switch is a leaf its callers rely on to leave rcx alone (tree init passes the first name
        // to a second call in rcx and reuses it), so it's extended in assembly, touching only rax.
        private void HookTreeNodeName(int offset)
        {
            _treeNodeName = _hooks!.CreateAsmHook(new[]
            {
                "use64",
                $"cmp edx, {TreeDarkKnight}",
                "jne notDarkKnight",
                $"mov rax, 0x{(long)DarkKnightNodeName:X}",
                "ret",
                "notDarkKnight:",
                $"cmp edx, {TreeOnionKnight}",
                "jne original",
                $"mov rax, 0x{(long)OnionKnightNodeName:X}",
                "ret",
                "original:",
            }, (long)_gameBase + offset, AsmHookBehaviour.ExecuteFirst).Activate();
        }

        // Tree index from the job, not the menu position: the menu holds one of Bard/Dancer and, on page 2, other jobs
        // entirely, while the tree has a fixed node per job. The game adds 1 for Dancer and Mime itself.
        private static int JobTreeIndex(int job) => job switch
        {
            >= 0x4A and <= 0x5B => job - 0x4A,
            0x5C or 0x5D => job - 0x4A - 1,
            0xA0 => TreeDarkKnight,
            0xA1 => TreeOnionKnight,
            _ => -1,
        };

        private unsafe void PopulateJobTreeSlotHook(nint tree, nint jobData, int index)
        {
            int treeIndex = JobTreeIndex(*(int*)(jobData + TreeJobDataJobOffset));
            _populateJobTreeSlot!.OriginalFunction(tree, jobData, treeIndex >= 0 ? treeIndex : index);
        }

        // Confirming a tree node returns to the job menu with that job selected; Dark Knight and Onion Knight have no
        // slot on the menu's first page, so return to the unit's current job instead.
        private unsafe void JobTreeInputHook(nint tree, nint state)
        {
            int hovered = *(int*)(tree + TreeHoverIndexOffset);
            if (hovered >= TreeDarkKnight && hovered < TreeJobCount &&
                (_isButtonTriggered!(0, 0, ConfirmButton, 0) != 0 || *(byte*)(tree + TreeClickedOffset) != 0))
            {
                int current = *(int*)(tree + TreeCurrentIndexOffset);
                *(int*)(tree + TreeHoverIndexOffset) = current >= 0 && current < TreeDarkKnight ? current : 0;
            }

            _jobTreeInput!.OriginalFunction(tree, state);
        }
    }
}

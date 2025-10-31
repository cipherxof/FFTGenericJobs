using Reloaded.Mod.Interfaces.Structs;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using GenericJobs.Template.Configuration;

namespace GenericJobs.Configuration
{
    public class Config : Configurable<Config>
    {
        /*[DisplayName("Extra Jobs (Decimal or Hex IDs)")]
        [Description("Comma-separated list of job IDs. Currently no more than two are supported")]
        [DefaultValue("A0")]
        public string ExtraJobsHex { get; set; } = "A0";*/
    }

    /// <summary>
    /// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
    /// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
    /// </summary>
    public class ConfiguratorMixin : ConfiguratorMixinBase
    {
        // 
    }
}

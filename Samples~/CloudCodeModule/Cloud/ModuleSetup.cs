using Unity.Services.CloudCode.Apis.Extensions;
using Unity.Services.CloudCode.Core;

namespace IntegrityModule.Cloud
{
    /// <summary>
    /// Registers the UGS game-api client so the module can read server-side services (e.g. Remote
    /// Config, Cloud Save) if your validation needs them. Remove if you don't.
    /// </summary>
    public class ModuleSetup : ICloudCodeSetup
    {
        public void Setup(ICloudCodeConfig config)
        {
            config.AddGameApiClient();
        }
    }
}

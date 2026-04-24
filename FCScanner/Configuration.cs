using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace FCScanner
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        // Added the missing properties to fix the CS1061 errors
        public bool IsConfigWindowMovable { get; set; } = true;
        public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;

        [NonSerialized]
        private IDalamudPluginInterface? PluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.PluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.PluginInterface!.SavePluginConfig(this);
        }
    }
}
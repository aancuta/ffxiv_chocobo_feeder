using Dalamud.Configuration;
using System;

namespace EasyStables;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string BarkServer { get; set; } = ""; // Bark Server

    public int userDelayMs { get; internal set; } = 1000; // 1s

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

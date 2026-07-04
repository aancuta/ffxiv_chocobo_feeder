using Dalamud.Configuration;
using System;

namespace EasyStables;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string BarkServer { get; set; } = ""; // Bark Server

    public int userDelayMs { get; internal set; } = 1000; // 1s

    public int birdTimerDelayMin { get; internal set; } = 1; // 1 min
    public int birdTimerDelayMax { get; internal set; } = 15; // 15 min

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using EasyStables;
using Serilog;
using System;
using System.Numerics;

namespace SamplePlugin
{
    internal class Windows
    {
        internal class MainWindow : Window
        {
            private string barkServer = "";
            private int delayMs = 0;
            private int birdTimerDelayMin = 0;
            private int birdTimerDelayMax = 0;

            private Plugin plugin;
            private Configuration config;

            public MainWindow(Plugin plugin, Configuration config) : base("EasyStables Window")
            {
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(600.0f, 250.0f),
                    MaximumSize = new Vector2(600.0f, 250.0f),
                };
                this.plugin = plugin;
                this.config = config;
                barkServer = config.BarkServer;
                delayMs = config.userDelayMs;
                birdTimerDelayMin = config.birdTimerDelayMin;
                birdTimerDelayMax = config.birdTimerDelayMax;
            }

            public override void Draw()
            {
                ImGui.Text("Use the Server Info bar at the top to toggle the plugin.");

                ImGui.InputInt("User Delay (ms)", ref delayMs);
                ImGui.InputText("Bark Server URL", ref barkServer);
                ImGui.SameLine();
                if(ImGui.Button("Test"))
                {
                    // Test the Bark Server URL
                    plugin.SendToBark("Test title", "Test content");
                }

                ImGui.InputInt("Min Bird timer delay (min)", ref birdTimerDelayMin);
                ImGui.InputInt("Max Bird timer delay (min)", ref birdTimerDelayMax);

                if (ImGui.Button("Save Config"))
                {
                    Log.Information("Save triggered!");
                    config.userDelayMs = delayMs;
                    config.BarkServer = barkServer;
                    config.birdTimerDelayMin = birdTimerDelayMin;
                    config.birdTimerDelayMax = birdTimerDelayMax;
                    config.Save();
                }
            }

            internal void Dispose()
            {
                config.Save();
            }
        }
    }
}

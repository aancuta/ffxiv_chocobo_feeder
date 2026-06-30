using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using EasyStables;
using System;
using System.Numerics;

namespace SamplePlugin
{
    internal class Windows
    {
        internal class MainWindow : Window
        {
            private Plugin plugin;
            private Configuration config;

            private int userDelayMs;
            private int birdTimer;
            private string barkServerURL = "";

            public MainWindow(Plugin plugin, Configuration config) : base("EasyStables Window")
            {
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(500.0f, 505.0f),
                    MaximumSize = new Vector2(500.0f, 505.0f),
                };
                this.plugin = plugin;
                this.config = config;
                userDelayMs = 1000; // default to 1000
            }

            public int UserDelayMsPreference { get => userDelayMs; internal set => userDelayMs = value; }
            public string BarkServerURLPreference { get => barkServerURL; internal set => barkServerURL = value; }

            public override void Draw()
            {
                ImGui.Text("Use the Server Info bar at the top to toggle the plugin.");

                ImGui.InputInt("User Delay (ms)", ref userDelayMs);

                ImGui.InputText("Bark Server URL", ref barkServerURL);
                ImGui.SameLine();
                if(ImGui.Button("Test"))
                {
                    // Test the Bark Server URL
                    plugin.SendToBark("Test title", "Test content");
                }
            }

            private void SaveConfig()
            {
                config.userDelayMs = userDelayMs;
                config.BarkServer = barkServerURL;
                config.Save();
            }

            internal void Dispose()
            {
            }
        }
    }
}

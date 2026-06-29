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

            private int userDelayMs;
            private int birdTimerMiliseconds;

            public MainWindow(Plugin plugin) : base("EasyStables Window")
            {
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(500.0f, 505.0f),
                    MaximumSize = new Vector2(500.0f, 505.0f),
                };
                this.plugin = plugin;
                userDelayMs = 1000; // default to 1000
                birdTimerMiliseconds = 61 * 60 * 1000; // default to 61 minutes
            }

            public int UserDelayMsPreference { get => userDelayMs; internal set => userDelayMs = value; }
            public int BirdTimerMilisecondsPreference { get => birdTimerMiliseconds; internal set => birdTimerMiliseconds = value; }

            public override void Draw()
            {
                ImGui.InputInt("User Delay (ms)", ref userDelayMs);

                ImGui.InputInt("Bird Timer (ms)", ref birdTimerMiliseconds);
            }

            internal void Dispose()
            {

            }
        }
    }
}

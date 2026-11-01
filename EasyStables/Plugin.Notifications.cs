using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace EasyStables;

public sealed partial class Plugin
{
    private HttpClient httpClient = new HttpClient();

    public async Task SendToBark(string title, string content)
    {
        Log.Info(Configuration.BarkServer);
        if (string.IsNullOrEmpty(Configuration.BarkServer))
        {
            // don't error out for no server URL, just don't send the notification
            return;
        }

        string serverUrl = Configuration.BarkServer.TrimEnd('/');
        var url = $"{serverUrl}/{title}/{content}?" +
                 $"sound=ff14&icon=https://cdn2.steamgriddb.com/icon/5f268dfb0fbef44de0f668a022707b86/32/256x256.png" + // Customize notify ring and icon
                 "&level=timeSensitive"; // iOS Time Sensitive
        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }

    private static HashSet<Dalamud.Game.Text.XivChatType> DalamudChatsToMonitor = new HashSet<Dalamud.Game.Text.XivChatType> {
        Dalamud.Game.Text.XivChatType.GmSay,
        Dalamud.Game.Text.XivChatType.GmShout,
        Dalamud.Game.Text.XivChatType.GmYell,
        Dalamud.Game.Text.XivChatType.GmTell,
        Dalamud.Game.Text.XivChatType.GmParty,
        Dalamud.Game.Text.XivChatType.TellIncoming,
    };

    private void ChatMessage(IChatMessage message)
    {
        if (!DalamudChatsToMonitor.Contains(message.LogKind))
        {
            return;
        }
        SendToBark("Chat Message", $"{message.Sender}: {message.Message.ToString()}");
    }
}

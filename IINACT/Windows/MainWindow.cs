using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIV_ACT_Plugin.Config;
using RainbowMage.OverlayPlugin;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using NAudio.Wave;
using RainbowMage.OverlayPlugin.EventSources;

namespace IINACT.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin { get; }

    private int selectedOverlayIndex;

    public MainWindow(Plugin plugin) : base($"IINACT v{plugin.Version}")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(307, 207),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Plugin = plugin;
    }

    public IPluginConfig? OverlayPluginConfig { get; set; }
    public BuiltinEventConfig? OverlayPluginEventConfig { get; set; }
    public IReadOnlyList<RainbowMage.OverlayPlugin.IOverlayTemplate>? OverlayPresets { get; set; }
    private string[]? OverlayNames => OverlayPresets?.Select(x => x.Name).ToArray();
    public RainbowMage.OverlayPlugin.WebSocket.ServerController? Server { get; set; }

    public void Dispose() { }

    private static string ParseFilterName(ParseFilterMode filter)
        => filter switch
        {
            ParseFilterMode.None => "無",
            ParseFilterMode.Self => "僅自己",
            ParseFilterMode.Party => "僅小隊成員",
            ParseFilterMode.Alliance => "僅團隊成員",
            _ => filter.ToString(),
        };

    public override void Draw()
    {
        using var bar = ImRaii.TabBar("settingsTabs");
        if (!bar) return;

        DrawMainWindow();
        DrawParseSettings();
        DrawTtsSettings();
        DrawWebSocketSettings();
    }

    private void DrawMainWindow()
    {
        using var tab = ImRaii.TabItem("狀態###Status");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "OverlayPlugin 狀態：");
        ImGuiHelpers.ScaledRelativeSameLine(155);
        ImGui.Text(Plugin.OverlayPluginStatus);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "懸浮窗 URI 產生器：");

        var comboWidth = ImGui.GetWindowWidth() * 0.8f;
        
        var selectedIndexOverlayName = OverlayNames?[selectedOverlayIndex] ?? "";
        var selectedOverlayName = Plugin.Configuration.SelectedOverlay ?? selectedIndexOverlayName;
        if (selectedOverlayName != selectedIndexOverlayName)
            for (var i = 0; i < OverlayNames?.Length; i++)
                if (OverlayNames?[i] == selectedOverlayName) 
                    selectedOverlayIndex = i;
        
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("懸浮窗###Overlay", selectedOverlayName))
        {
            for (var i = 0; i < OverlayNames?.Length; i++)
            {
                var currentOverlayName = OverlayNames?[i] ?? "";
                if (ImGui.Selectable(currentOverlayName, currentOverlayName == selectedOverlayName))
                {
                    selectedOverlayIndex = i;
                    Plugin.Configuration.SelectedOverlay = currentOverlayName;
                    Plugin.Configuration.Save();
                }
            }

            ImGui.EndCombo();
        }

        var selectedOverlay = OverlayPresets?[selectedOverlayIndex];
        Uri.TryCreate($"ws://{Server?.Address}:{Server?.Port}/ws", UriKind.Absolute, out var webSocketServer);
        var overlayUri = selectedOverlay?.ToOverlayUri(webSocketServer);
        var overlayUriString = overlayUri?.ToString() ?? "<無法產生 URI>";

        ImGui.SetNextItemWidth(comboWidth);
        ImGui.InputText("URI###OverlayURI", ref overlayUriString, 1000, ImGuiInputTextFlags.ReadOnly);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var serverStatus = Server is null ? "正在初始化……" : "已停止";

        if (Server?.Running ?? false)
            serverStatus = $"正在監聽 {Server?.Address}:{Server?.Port}";

        if (Server?.Failed ?? false)
        {
            serverStatus = Server.LastException?.Message ?? "啟動失敗";
            if (Server.LastException is SocketException { ErrorCode: 10048 })
                serverStatus = $"連接埠 {Server?.Port} 已被占用";
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "WebSocket 伺服器：");
        ImGuiHelpers.ScaledRelativeSameLine(155);
        ImGui.Text(serverStatus);
        ImGui.GetWindowDpiScale();

        if (Server?.Running ?? false)
        {
            if (ImGui.Button("停止###Stop"))
                Server.Stop();

            ImGui.SameLine();

            if (ImGui.Button("重新啟動###Restart"))
                Server.Restart();
        }
        else if (Server is not null)
        {
            if (ImGui.Button("啟動###Start"))
                Server.Start();
        }
    }

     private void DrawParseSettings()
    {
        using var tab = ImRaii.TabItem("解析器###Parser");
        if (!tab) return;

        ImGui.Spacing();
        var elementWidth = ImGui.GetWindowWidth() - (150 * ImGuiHelpers.GlobalScale);
        var logFilePath = Plugin.Configuration.LogFilePath;
        ImGui.SetNextItemWidth(elementWidth);
        ImGui.InputText("記錄檔路徑###Log File Path", ref logFilePath, 200, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiComponents.DisabledButton(FontAwesomeIcon.Folder))
        {
            Plugin.FileDialogManager.OpenFolderDialog("選擇記錄檔儲存資料夾", (success, path) =>
            {
                if (!success) return;
                Plugin.Configuration.LogFilePath = path;
                Plugin.Configuration.Save();
            }, Plugin.Configuration.LogFilePath);
        }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(elementWidth);
        if (ImGui.BeginCombo("解析篩選###Parse Filter",
                             ParseFilterName((ParseFilterMode)Plugin.Configuration.ParseFilterMode)))
        {
            foreach (var filter in Enum.GetValues<ParseFilterMode>())
                if (ImGui.Selectable($"{ParseFilterName(filter)}###{filter}",
                                     (ParseFilterMode)Plugin.Configuration.ParseFilterMode == filter))
                {
                    Plugin.Configuration.ParseFilterMode = (int)filter;
                    Plugin.Configuration.Save();
                }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        
        var writeLogFile = Plugin.Configuration.WriteLogFile;
        if (ImGui.Checkbox("寫入網路記錄檔###Write out network log file", ref writeLogFile))
        {
            Plugin.Configuration.WriteLogFile = writeLogFile;
            Plugin.Configuration.Save();
        }

        var disablePvp = Plugin.Configuration.DisablePvp;
        if (ImGui.Checkbox("在 PvP 中停用網路記錄檔寫入###Disable writing out network log file in PvP", ref disablePvp))
        {
            if (Plugin.ClientState.IsPvP && disablePvp) Plugin.Configuration.DisableWritingPvpLogFile = true;

            Plugin.Configuration.DisablePvp = disablePvp;
            Plugin.Configuration.Save();
        }

        var logChatMessages = Plugin.Configuration.LogChatMessages;
        if (ImGui.Checkbox("在記錄檔中包含聊天與回音訊息###Include chat and echo messages in log files", ref logChatMessages))
        {
            Plugin.Configuration.LogChatMessages = logChatMessages;
            Plugin.SetChatMessageLoggingEnabled(logChatMessages);
            Plugin.Configuration.Save();
        }

        var autoDeleteNetworkLogs = Plugin.Configuration.AutoDeleteNetworkLogs;
        if (ImGui.Checkbox("自動刪除舊的網路記錄檔###Automatically delete old network log files", ref autoDeleteNetworkLogs))
        {
            Plugin.Configuration.AutoDeleteNetworkLogs = autoDeleteNetworkLogs;
            Plugin.Configuration.Save();
        }

        if (autoDeleteNetworkLogs)
        {
            var networkLogRetentionDays = Plugin.Configuration.NetworkLogRetentionDays;
            ImGui.Text("刪除早於下列天數的記錄檔");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(30 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("天###days", ref networkLogRetentionDays))
            {
                Plugin.Configuration.NetworkLogRetentionDays = Math.Clamp(networkLogRetentionDays, 1, 3650);
                Plugin.Configuration.Save();
            }
        }

        var disableDamageShield = Plugin.Configuration.DisableDamageShield;
        if (ImGui.Checkbox("停用傷害護盾估算###Disable Damage Shield Estimates", ref disableDamageShield))
        {
            Plugin.Configuration.DisableDamageShield = disableDamageShield;
            Plugin.Configuration.Save();
        }

        var disableCombinePets = Plugin.Configuration.DisableCombinePets;
        if (ImGui.Checkbox("停用將寵物傷害合併至主人###Disable Combine Pets with Owners", ref disableCombinePets))
        {
            Plugin.Configuration.DisableCombinePets = disableCombinePets;
            Plugin.Configuration.Save();
        }

        var endEncounterOutOfCombat = OverlayPluginEventConfig?.EndEncounterOutOfCombat ?? true;
        if (ImGui.Checkbox("離開戰鬥後自動結束遭遇戰###End encounter automatically after leaving combat", ref endEncounterOutOfCombat))
        {
            if (OverlayPluginEventConfig is not null)
            {
                OverlayPluginEventConfig.EndEncounterOutOfCombat = endEncounterOutOfCombat;
                if (OverlayPluginConfig is not null)
                {
                    OverlayPluginEventConfig.SaveConfig(OverlayPluginConfig);
                    OverlayPluginConfig.Save();
                }
            }
        }

        var showDebug = Plugin.Configuration.ShowDebug;
        if (ImGui.Checkbox("顯示偵錯選項###Show Debug Options", ref showDebug))
        {
            Plugin.Configuration.ShowDebug = showDebug;
            Plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var playerCharacterName = Plugin.Configuration.PlayerCharacterName;
        ImGui.SetNextItemWidth(elementWidth);
        if (ImGui.InputText("玩家名稱###Player name", ref playerCharacterName, 100))
        {
            Plugin.Configuration.PlayerCharacterName = playerCharacterName;
            Plugin.Configuration.Save();
        }

        if (!showDebug) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var simulateIndividualDoTCrits = Plugin.Configuration.SimulateIndividualDoTCrits;
        if (ImGui.Checkbox("模擬每次持續傷害暴擊###Simulate Individual DoT Crits", ref simulateIndividualDoTCrits))
        {
            Plugin.Configuration.SimulateIndividualDoTCrits = simulateIndividualDoTCrits;
            Plugin.Configuration.Save();
        }

        var showRealDoTTicks = Plugin.Configuration.ShowRealDoTTicks;
        if (ImGui.Checkbox("同時顯示「實際」持續傷害跳數###Also Show 'Real' DoT Ticks", ref showRealDoTTicks))
        {
            Plugin.Configuration.ShowRealDoTTicks = showRealDoTTicks;
            Plugin.Configuration.Save();
        }
    }

    private void DrawTtsSettings()
    {
        using var tab = ImRaii.TabItem("文字轉語音###Text to Speech");
        if (!tab) return;
        
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Google 文字轉語音：");
        ImGui.Spacing();

        var forceGoogleTts = Plugin.Configuration.ForceGoogleTts;
        if (ImGui.Checkbox("強制使用 Google TTS 而非 SAPI###Force Google TTS instead of SAPI", ref forceGoogleTts))
        {
            Plugin.Configuration.ForceGoogleTts = forceGoogleTts;
            Plugin.Configuration.Save();
        }

        ImGui.Spacing();

        var googleTtsLanguage = Plugin.Configuration.GoogleTtsLanguage;
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("語言###Language", ref googleTtsLanguage, 10))
        {
            Plugin.Configuration.GoogleTtsLanguage = googleTtsLanguage;
            Plugin.Configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "(e.g. ja, en, de, fr, ko)");
        ImGui.Spacing();

        var ttsDeviceCount = WaveOut.DeviceCount;
        var currentDevice = Plugin.Configuration.TtsPlaybackDevice;
        var currentDeviceName = currentDevice == -1 ? "預設" : WaveOut.GetCapabilities(currentDevice).ProductName;
        
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);

        if (ImGui.BeginCombo("播放裝置###Playback Device", currentDeviceName))
        {
            if (ImGui.Selectable("預設###Default", currentDevice == -1))
            {
                Plugin.Configuration.TtsPlaybackDevice = -1;
                Plugin.Configuration.Save();
            }

            for (var i = 0; i < ttsDeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                if (ImGui.Selectable(caps.ProductName, currentDevice == i))
                {
                    Plugin.Configuration.TtsPlaybackDevice = i;
                    Plugin.Configuration.Save();
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawWebSocketSettings()
    {
        using var tab = ImRaii.TabItem("WebSocket 伺服器###WebSocket Server");
        if (!tab) return;
        
        ImGui.Spacing();
        var wsServerIp = OverlayPluginConfig?.WSServerIP ?? "";
        ImGui.InputText("IP", ref wsServerIp, 100, ImGuiInputTextFlags.None);

        if (IPAddress.TryParse(wsServerIp, out var address))
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerIP = address.ToString();
        }
        else if (wsServerIp == "*")
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerIP = "*";
        }

        var wsServerPort = OverlayPluginConfig?.WSServerPort.ToString() ?? "";
        ImGui.InputText("連接埠###Port", ref wsServerPort, 100, ImGuiInputTextFlags.None);

        if (int.TryParse(wsServerPort, out var port))
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerPort = port;
        }

        OverlayPluginConfig?.Save();
    }

}

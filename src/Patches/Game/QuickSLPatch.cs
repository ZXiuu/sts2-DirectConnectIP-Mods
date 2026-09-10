#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using DirectConnectIP.Helpers;
using DirectConnectIP.Network;

namespace DirectConnectIP.Patches.Game;

// =============================================
// 自定义网络消息（通过游戏 INetMessage 系统发送）
// =============================================
internal struct QuickSLWarningMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { }
    public void Deserialize(PacketReader r) { }
}

// =============================================
// UIManager — ConfirmDialog（参照反编译代码）
// =============================================
public static class UIManager_QuickSL
{
    public static void ShowConfirmDialog(string title, string message, Action onConfirm, Action? onCancel = null)
    {
        Callable.From(() =>
        {
            var dialog = new ConfirmationDialog();
            dialog.Title = title;
            dialog.DialogText = message;
            dialog.OkButtonText = "同意";
            dialog.CancelButtonText = "拒绝";
            dialog.MinSize = new Vector2I(600, 120);
            dialog.AddThemeFontSizeOverride("title_font_size", 32);

            var label = dialog.GetLabel();
            if (label != null)
            {
                label.AddThemeFontSizeOverride("font_size", 24);
                label.HorizontalAlignment = HorizontalAlignment.Center;
            }

            dialog.Connect(AcceptDialog.SignalName.Confirmed, Callable.From(() =>
            {
                onConfirm();
                dialog.QueueFree();
            }));
            dialog.Connect(AcceptDialog.SignalName.Canceled, Callable.From(() =>
            {
                onCancel?.Invoke();
                dialog.QueueFree();
            }));

            if (Engine.GetMainLoop() is SceneTree tree)
            {
                tree.Root.AddChild(dialog);
                dialog.PopupCentered();
            }
        }).CallDeferred();
    }
}

// =============================================
// QuickSLFlow — 核心流程（参照 MultiplayerQuickSL）
// =============================================
public static class QuickSLFlow
{
    // 状态标记
    public static bool ExpectQuickSL;
    public static bool HostPendingQuickSL;
    public static bool IsAutoQuickSLHost;
    public static bool IsAutoQuickSLClient;
    public static bool IsSteamRoom = true;
    public static ulong HostLocalId;

    public static void InitiateFromHost()
    {
        Log.Info($"[DirectConnectIP] QuickSL: 房主发起");
        ExecuteQuickSL();
    }

    public static void OnClientRequestedSL()
    {
        // 客机请求由 DirectHost.HandleQuickSLRequest 直接处理
        // 此方法保留用于 ReactionMessage 信号路由
        Log.Debug("[DirectConnectIP] QuickSL: OnClientRequestedSL");
    }

    public static void ExecuteQuickSL()
    {
        Log.Info("[DirectConnectIP] QuickSL: 执行 SL");

        HostPendingQuickSL = true;
        IsAutoQuickSLHost = true;
        IsSteamRoom = !string.IsNullOrEmpty(RunManager.Instance.NetService?.GetRawLobbyIdentifier());
        HostLocalId = RunManager.Instance.NetService?.NetId ?? ModEntry.Config.LocalPlayerId;

        // 发送预警给客机
        RunManager.Instance.NetService?.SendMessage(new QuickSLWarningMessage());

        // 找到暂停菜单
        NPauseMenu? targetMenu = null;
        if (Engine.GetMainLoop() is SceneTree tree)
            targetMenu = NodeHelper.FindChildNode<NPauseMenu>(tree.Root);

        TaskHelper.RunSafely(DelayedDisconnectAndReturn(targetMenu));
    }

    private static async Task DelayedDisconnectAndReturn(NPauseMenu? targetMenu)
    {
        // 延迟100ms等信号发出
        await Task.Delay(100);

        // 强制保存
        try
        {
            var saveMethod = typeof(RunManager).GetMethod("SaveRun",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            saveMethod?.Invoke(RunManager.Instance, [true]);
            Log.Info("[DirectConnectIP] QuickSL: 已强制存档");
        }
        catch (Exception ex) { Log.Warn($"[DirectConnectIP] QuickSL: 强制存档跳过: {ex.Message}"); }

        Callable.From(() =>
        {
            // 返回主菜单
            if (GodotObject.IsInstanceValid(targetMenu) && targetMenu!.IsInsideTree())
            {
                var closeMethod = typeof(NPauseMenu).GetMethod("CloseToMenu",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (closeMethod != null)
                {
                    Log.Info("[DirectConnectIP] QuickSL: 调起 CloseToMenu");
                    closeMethod.Invoke(targetMenu, null);
                }
            }
            else
            {
                // 兜底
                Log.Warn("[DirectConnectIP] QuickSL: 走 ReturnToMainMenuAfterRun 兜底");
                var gameType = typeof(NGame);
                var instance = gameType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                gameType.GetMethod("ReturnToMainMenuAfterRun", BindingFlags.Instance | BindingFlags.Public)?.Invoke(instance, null);
            }

            DisconnectAllClients();
        }).CallDeferred();
    }

    private static void DisconnectAllClients()
    {
        try
        {
            var runLobby = typeof(RunManager).GetProperty("RunLobby", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(RunManager.Instance);
            if (runLobby == null) return;

            // 尝试 PlayerIds (0.110) 或 ConnectedPlayerIds (0.109)
            var playerIdsProp = runLobby.GetType().GetProperty("PlayerIds", BindingFlags.Instance | BindingFlags.Public)
                ?? runLobby.GetType().GetProperty("ConnectedPlayerIds", BindingFlags.Instance | BindingFlags.Public);

            var playerIds = playerIdsProp?.GetValue(runLobby) as System.Collections.Generic.IEnumerable<ulong>;
            if (playerIds == null) return;

            var hostService = RunManager.Instance.NetService as INetHostGameService;
            if (hostService == null) return;

            foreach (var id in playerIds)
            {
                if (id != hostService.NetId)
                {
                    hostService.DisconnectClient(id, NetError.Quit);
                    Log.Info($"[DirectConnectIP] QuickSL: 已踢出 {id}");
                }
            }
        }
        catch (Exception ex) { Log.Warn($"[DirectConnectIP] QuickSL: 断开客机失败: {ex.Message}"); }
    }
}

// =============================================
// 1. NPauseMenu._Ready — 注入按钮（参照反编译代码）
// =============================================
[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
public static class NPauseMenu_Ready_Patch
{
    public static void Postfix(NPauseMenu __instance)
    {
        try
        {
            var netService = RunManager.Instance?.NetService;
            if (netService == null || netService.Type == NetGameType.Singleplayer) return;

            var container = __instance.GetNode<Control>("%ButtonContainer");
            if (container == null || container.HasNode("MultiplayerQuickSLBtn")) return;

            // 从官方场景文件加载按钮（确保 _Ready 被调用以初始化输入处理）
            var buttonScene = GD.Load<PackedScene>("res://scenes/pause_menu/pause_menu_button.tscn");
            if (buttonScene == null)
            {
                Log.Warn("[DirectConnectIP] QuickSL: 无法加载按钮场景");
                return;
            }
            var slBtn = buttonScene.Instantiate<NPauseMenuButton>();
            slBtn.Name = "MultiplayerQuickSLBtn";

            // 设置文字
            var label = slBtn.GetNodeOrNull<Label>("Label");
            if (label != null)
                label.Text = netService.Type == NetGameType.Host ? "快速SL" : "请求快速SL";

            container.AddChild(slBtn);

            // 使用 Callable.From<NButton> 匹配 NClickableControl.Released 委托类型
            slBtn.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(btn =>
            {
                Log.Info("[DirectConnectIP] QuickSL: 按钮被点击");
                if (RunManager.Instance.NetService?.Type == NetGameType.Host)
                {
                    __instance.Visible = false;
                    QuickSLFlow.InitiateFromHost();
                }
                else
                {
                    DirectClient.Current?.SendQuickSLRequest();
                    Log.Info("[DirectConnectIP] QuickSL: 已向房主发送请求");

                    var resumeMethod = typeof(NPauseMenu).GetMethod("OnResumePressed",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    resumeMethod?.Invoke(__instance, new object[1]);
                }
            }));

            Log.Info($"[DirectConnectIP] QuickSL: 按钮注入成功 ({netService.Type})");
        }
        catch (Exception ex) { Log.Error($"[DirectConnectIP] QuickSL: 按钮注入失败: {ex}"); }
    }
}

// =============================================
// 2. NMainMenu._Ready — 自动建主
// =============================================
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class NMainMenu_Ready_Patch
{
    public static void Postfix(NMainMenu __instance)
    {
        if (!QuickSLFlow.HostPendingQuickSL) return;
        QuickSLFlow.HostPendingQuickSL = false;
        Log.Info("[DirectConnectIP] QuickSL: 房主回主菜单，自动建房");
        TaskHelper.RunSafely(AutoHost(__instance));
    }

    private static async Task AutoHost(NMainMenu mainMenu)
    {
        try
        {
            await Task.Delay(100); // 等文件解锁

            // 同步 DirectConnectIP 的 HostMode
            try
            {
                var ipMod = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "DirectConnectIP");
                if (ipMod != null)
                {
                    var settingsType = ipMod.GetType("DirectConnectIP.HostModeSettings");
                    var hostModeType = ipMod.GetType("DirectConnectIP.HostMode");
                    if (settingsType != null && hostModeType != null)
                    {
                        var modeValue = Enum.Parse(hostModeType, QuickSLFlow.IsSteamRoom ? "Steam" : "ENet");
                        settingsType.GetProperty("CurrentMode", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, modeValue);
                        settingsType.GetField("CurrentMode", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, modeValue);
                        Log.Info($"[DirectConnectIP] QuickSL: 同步 HostMode={modeValue}");
                    }
                }
            }
            catch { }

            // 读取存档
            var saveResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(QuickSLFlow.HostLocalId);
            if (!saveResult.Success || saveResult.SaveData == null)
            {
                Log.Error($"[DirectConnectIP] QuickSL: 读档失败 {saveResult.Status}");
                return;
            }

            // 获取 NSubmenuStack
            var stack = await NodeHelper.WaitAndFindNode<NSubmenuStack>(mainMenu.GetTree().Root);
            if (stack == null) { Log.Error("[DirectConnectIP] QuickSL: 找不到NSubmenuStack"); return; }

            // 获取 NMultiplayerSubmenu
            var getSubmenuMethod = typeof(NSubmenuStack).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetSubmenuType" && m.IsGenericMethodDefinition);
            var mpSubmenu = getSubmenuMethod?.MakeGenericMethod(typeof(NMultiplayerSubmenu)).Invoke(stack, null);
            if (mpSubmenu == null) { Log.Error("[DirectConnectIP] QuickSL: 获取NMultiplayerSubmenu失败"); return; }

            // 设置 _stack
            typeof(NSubmenu).GetField("_stack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(mpSubmenu, stack);

            // 调用 StartHostAsync
            var startHostMethod = typeof(NMultiplayerSubmenu).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "StartHostAsync");
            if (startHostMethod != null)
            {
                Log.Info("[DirectConnectIP] QuickSL: 调用 StartHostAsync");
                await (Task)startHostMethod.Invoke(mpSubmenu, [saveResult.SaveData])!;
            }
        }
        catch (Exception ex) { Log.Error($"[DirectConnectIP] QuickSL: AutoHost异常: {ex}"); }
    }
}

// =============================================
// 3. NMultiplayerLoadGameScreen — 自动准备
// =============================================
[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "OnSubmenuOpened")]
public static class NMultiplayerLoadGameScreen_Opened_Patch
{
    public static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        Log.Info($"[DirectConnectIP] QuickSL: 加载画面打开 IsClient={QuickSLFlow.IsAutoQuickSLClient} IsHost={QuickSLFlow.IsAutoQuickSLHost}");

        if (QuickSLFlow.IsAutoQuickSLClient)
        {
            QuickSLFlow.IsAutoQuickSLClient = false;
            TaskHelper.RunSafely(AutoClickReady(__instance, 100)); // AutoReadyClientDelayMs
        }
        else if (QuickSLFlow.IsAutoQuickSLHost)
        {
            CheckHostAutoReady(__instance);
        }
    }

    public static async Task AutoClickReady(NMultiplayerLoadGameScreen instance, int delayMs)
    {
        await Task.Delay(delayMs);
        if (!instance.IsInsideTree()) return;

        var embarkMethod = typeof(NMultiplayerLoadGameScreen).GetMethod("OnEmbarkPressed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        embarkMethod?.Invoke(instance, new object[1]);
        Log.Info("[DirectConnectIP] QuickSL: 自动准备完成");
    }

    public static void CheckHostAutoReady(NMultiplayerLoadGameScreen instance)
    {
        try
        {
            var lobbyField = typeof(NMultiplayerLoadGameScreen).GetField("_runLobby",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (lobbyField?.GetValue(instance) is not LoadRunLobby lobby) return;

            var runField = typeof(LoadRunLobby).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic);
            var run = runField?.GetValue(lobby) as SerializableRun;
            var playerCount = run?.Players?.Count ?? 0;

            var playersField = typeof(LoadRunLobby).GetField("_players", BindingFlags.Instance | BindingFlags.NonPublic);
            var players = playersField?.GetValue(lobby) as System.Collections.IList;
            var connectedCount = players?.Count ?? 0;

            Log.Info($"[DirectConnectIP] QuickSL: 房主检查就绪 {connectedCount}/{playerCount}");

            if (connectedCount >= playerCount)
            {
                QuickSLFlow.IsAutoQuickSLHost = false;
                TaskHelper.RunSafely(AutoClickReady(instance, 200)); // AutoReadyHostDelayMs
            }
        }
        catch (Exception ex) { Log.Warn($"[DirectConnectIP] QuickSL: CheckHostAutoReady异常: {ex.Message}"); }
    }
}

// 当有玩家连入时触发房主检查
[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "PlayerConnected")]
public static class NMultiplayerLoadGameScreen_PlayerConnected_Patch
{
    public static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        if (QuickSLFlow.IsAutoQuickSLHost)
        {
            NMultiplayerLoadGameScreen_Opened_Patch.CheckHostAutoReady(__instance);
        }
    }
}

// =============================================
// 4. NErrorPopup.Create — 拦截断线弹窗，自动重连
// =============================================
[HarmonyPatch(typeof(NErrorPopup), "Create", [typeof(NetErrorInfo)])]
public static class NErrorPopup_Create_Patch
{
    public static bool Prefix(NetErrorInfo info, ref NErrorPopup? __result)
    {
        if ((QuickSLFlow.ExpectQuickSL || info.GetReason() == NetError.Quit)
            && RunManager.Instance.NetService?.Type == NetGameType.Client)
        {
            QuickSLFlow.ExpectQuickSL = false;
            QuickSLFlow.IsSteamRoom = !string.IsNullOrEmpty(RunManager.Instance.NetService?.GetRawLobbyIdentifier());
            Log.Info("[DirectConnectIP] QuickSL: 拦截断线弹窗，启动自动重连");

            QuickSLFlow.IsAutoQuickSLClient = true;
            TaskHelper.RunSafely(AutoReconnect());
            __result = null;
            return false;
        }
        return true;
    }

    private static async Task AutoReconnect()
    {
        try
        {
            var delayMs = QuickSLFlow.IsSteamRoom ? 500 : 500;
            await Task.Delay(delayMs);

            if (QuickSLFlow.IsSteamRoom)
            {
                Log.Error("[DirectConnectIP] QuickSL: Steam重连暂不支持");
                return;
            }

            // IP 直连重连
            var recentServers = ModEntry.Config.RecentServers;
            if (recentServers.Count == 0) { Log.Warn("[DirectConnectIP] QuickSL: 无历史IP"); return; }

            var parts = recentServers[0].Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)33771;

            var initializer = new DirectClientConnectionInitializer(host, port, ModEntry.Config.LocalPlayerId);
            Log.Info($"[DirectConnectIP] QuickSL: 自动重连 {host}:{port}");
            await ConnectionService.ConnectAsync(initializer);
        }
        catch (Exception ex) { Log.Error($"[DirectConnectIP] QuickSL: 自动重连失败: {ex}"); }
    }
}

// =============================================
// 5. ReactionMessage.Deserialize — 拦截信号
// =============================================
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Flavor.ReactionMessage), "Deserialize")]
public static class ReactionMessage_Deserialize_Patch
{
    public static void Postfix(ref MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Flavor.ReactionMessage __instance)
    {
        // 通过 heart reaction 的位置编码传递 QuickSL 信号
        var y = __instance.normalizedPosition.Y;
        if (y is 2.5f)      QuickSLFlow.ExpectQuickSL = true;  // 预警
        else if (y is 1.5f) QuickSLFlow.OnClientRequestedSL();  // 客机请求
        else return;

        Log.Debug($"[DirectConnectIP] QuickSL: 收到信号 y={y}");
    }
}

// =============================================
// 6. 辅助类
// =============================================
public static class NodeHelper
{
    public static async Task<T?> WaitAndFindNode<T>(Node parent, int maxRetries = 20, int delayMs = 100) where T : Node
    {
        for (int i = 0; i < maxRetries; i++)
        {
            if (!GodotObject.IsInstanceValid(parent)) return null;
            var result = FindChildNode<T>(parent);
            if (result != null) return result;
            await Task.Delay(delayMs);
        }
        return null;
    }

    public static T? FindChildNode<T>(Node parent) where T : Node
    {
        if (!GodotObject.IsInstanceValid(parent)) return null;
        if (parent is T result) return result;
        foreach (Node child in parent.GetChildren())
        {
            var found = FindChildNode<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}

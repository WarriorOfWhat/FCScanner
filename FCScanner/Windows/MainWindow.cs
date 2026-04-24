using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace FCScanner.Windows;

public unsafe class MainWindow : Window, IDisposable
{
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly string configPath;

    // Scanner State
    private List<FCMember> memberList = new();
    private bool isScanning = false;
    private bool isTurbo = true;
    private bool allowPageSwap = false;
    private int stagnationCount = 0;
    private int lastMemberCount = 0;
    private int currentPage = 1;
    private bool isPausingForSwap = false;
    private const int TARGET_PER_PAGE = 199;

    private string editingBuffer = "";
    private string? editingName = null;

    private Stopwatch scanTimer = new();
    private Stopwatch pauseTimer = new();
    private Stopwatch swapCooldown = new();

    // Activity Data
    private Dictionary<string, MemberActivity> activityDb = new();
    private bool showActivityConfirm = false;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_NUMPAD2 = 0x62;
    private const int VK_NUMPAD4 = 0x64;
    private const int VK_NUMPAD6 = 0x66;

    private readonly Regex nameRegex = new(@"[A-Z][a-zA-Z'-]+ [A-Z][a-zA-Z'-]+");
    private readonly Regex timeRegex = new(@"^\d+[dhms]$");
    private readonly string[] rankBlacklist = { "Master", "Officer", "Member", "Recruit", "Leader" };

    public MainWindow(Plugin plugin, IGameGui gameGui, IChatGui chatGui, string configDir) : base("Keepers Toolkit")
    {
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(650, 700) };
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.configPath = Path.Combine(configDir, "activity_data.json");

        LoadActivityData();
        this.swapCooldown.Start();
        this.chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        this.chatGui.ChatMessage -= OnChatMessage;
        SaveActivityData();
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        string msg = message.TextValue;
        bool isLogin = msg.Contains("has logged in.");

        if (isLogin)
        {
            string name = msg.Replace(" has logged in.", "").Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (!activityDb.ContainsKey(name))
                activityDb[name] = new MemberActivity { Name = name };

            activityDb[name].LoginsPerHour[DateTime.Now.Hour]++;
            activityDb[name].LastSeen = DateTime.Now;
            SaveActivityData();
        }
    }

    public override void Draw()
    {
        if (Dalamud.Bindings.ImGui.ImGui.BeginTabBar("ToolkitTabs"))
        {
            if (Dalamud.Bindings.ImGui.ImGui.BeginTabItem("Roster Scanner"))
            {
                DrawRosterTab();
                Dalamud.Bindings.ImGui.ImGui.EndTabItem();
            }
            if (Dalamud.Bindings.ImGui.ImGui.BeginTabItem("Activity Heatmap"))
            {
                DrawActivityTab();
                Dalamud.Bindings.ImGui.ImGui.EndTabItem();
            }
            Dalamud.Bindings.ImGui.ImGui.EndTabBar();
        }
    }

    private void DrawRosterTab()
    {
        int missingCount = memberList.Count(m =>
            string.IsNullOrEmpty(m.Location) || m.Location.Contains("Unable to Retrieve") || m.Location.Contains("Scanning"));

        string status = isPausingForSwap ? "PAUSING..." : (isScanning ? "TURBO SCANNING..." : "IDLE");
        Dalamud.Bindings.ImGui.ImGui.TextColored(ImGuiColors.ParsedGold, $"Total Found: {memberList.Count} | Page: {currentPage} | {status}");

        if (missingCount > 0)
            Dalamud.Bindings.ImGui.ImGui.TextColored(ImGuiColors.DalamudRed, $"Missing/Incomplete Locations: {missingCount}");
        else if (memberList.Count > 0)
            Dalamud.Bindings.ImGui.ImGui.TextColored(ImGuiColors.HealerGreen, "Roster data 100% complete!");

        Dalamud.Bindings.ImGui.ImGui.Separator();

        Dalamud.Bindings.ImGui.ImGui.Checkbox("Turbo", ref isTurbo);
        Dalamud.Bindings.ImGui.ImGui.SameLine();
        Dalamud.Bindings.ImGui.ImGui.Checkbox("Allow Page Swap", ref allowPageSwap);

        if (Dalamud.Bindings.ImGui.ImGui.Button(isScanning ? "STOP SCAN" : "START SCAN", new Vector2(120, 30)))
        {
            isScanning = !isScanning;
            if (isScanning) { scanTimer.Restart(); lastMemberCount = memberList.Count; stagnationCount = 0; }
        }

        Dalamud.Bindings.ImGui.ImGui.SameLine();
        if (Dalamud.Bindings.ImGui.ImGui.Button("CLEAR LIST", new Vector2(100, 30))) { memberList.Clear(); currentPage = 1; }

        Dalamud.Bindings.ImGui.ImGui.SameLine();
        if (Dalamud.Bindings.ImGui.ImGui.Button("EXPORT CSV", new Vector2(100, 30))) ExportToCsv();

        RunScannerLogic();

        var tableFlags = Dalamud.Bindings.ImGui.ImGuiTableFlags.Borders | Dalamud.Bindings.ImGui.ImGuiTableFlags.RowBg |
                         Dalamud.Bindings.ImGui.ImGuiTableFlags.ScrollY | Dalamud.Bindings.ImGui.ImGuiTableFlags.Sortable;

        if (Dalamud.Bindings.ImGui.ImGui.BeginTable("RosterTable", 2, tableFlags, new Vector2(0, -1)))
        {
            Dalamud.Bindings.ImGui.ImGui.TableSetupColumn("Name", Dalamud.Bindings.ImGui.ImGuiTableColumnFlags.DefaultSort);
            Dalamud.Bindings.ImGui.ImGui.TableSetupColumn("Location");
            Dalamud.Bindings.ImGui.ImGui.TableHeadersRow();

            HandleSorting();

            foreach (var m in memberList)
            {
                Dalamud.Bindings.ImGui.ImGui.TableNextRow();
                Dalamud.Bindings.ImGui.ImGui.TableNextColumn();
                Dalamud.Bindings.ImGui.ImGui.TextUnformatted(m.Name);

                Dalamud.Bindings.ImGui.ImGui.TableNextColumn();
                if (editingName == m.Name)
                {
                    Dalamud.Bindings.ImGui.ImGui.SetKeyboardFocusHere();
                    if (Dalamud.Bindings.ImGui.ImGui.InputText($"##edit_{m.Name}", ref editingBuffer, 100, Dalamud.Bindings.ImGui.ImGuiInputTextFlags.EnterReturnsTrue))
                    {
                        m.Location = editingBuffer;
                        editingName = null;
                    }
                    if (Dalamud.Bindings.ImGui.ImGui.IsItemDeactivated() && !Dalamud.Bindings.ImGui.ImGui.IsItemDeactivatedAfterEdit())
                        editingName = null;
                }
                else
                {
                    bool isBad = string.IsNullOrEmpty(m.Location) || m.Location.Contains("Unable to Retrieve") || m.Location.Contains("Scanning");

                    if (isBad)
                        Dalamud.Bindings.ImGui.ImGui.TextColored(ImGuiColors.DalamudRed, string.IsNullOrEmpty(m.Location) ? "Scanning..." : m.Location);
                    else
                        Dalamud.Bindings.ImGui.ImGui.TextUnformatted(m.Location);

                    if (Dalamud.Bindings.ImGui.ImGui.IsItemHovered() && Dalamud.Bindings.ImGui.ImGui.IsMouseDoubleClicked(0))
                    {
                        editingName = m.Name;
                        editingBuffer = m.Location;
                    }
                }
            }
            Dalamud.Bindings.ImGui.ImGui.EndTable();
        }
    }

    private void DrawActivityTab()
    {
        if (!showActivityConfirm)
        {
            if (Dalamud.Bindings.ImGui.ImGui.Button("Clear All Activity Data")) showActivityConfirm = true;
        }
        else
        {
            Dalamud.Bindings.ImGui.ImGui.TextColored(ImGuiColors.DalamudRed, "Wipe login history?");
            Dalamud.Bindings.ImGui.ImGui.SameLine();
            if (Dalamud.Bindings.ImGui.ImGui.Button("YES, CLEAR IT")) { activityDb.Clear(); SaveActivityData(); showActivityConfirm = false; }
            Dalamud.Bindings.ImGui.ImGui.SameLine();
            if (Dalamud.Bindings.ImGui.ImGui.Button("CANCEL")) showActivityConfirm = false;
        }

        Dalamud.Bindings.ImGui.ImGui.Separator();

        if (Dalamud.Bindings.ImGui.ImGui.BeginTable("HeatmapTable", 26, Dalamud.Bindings.ImGui.ImGuiTableFlags.Borders | Dalamud.Bindings.ImGui.ImGuiTableFlags.ScrollY | Dalamud.Bindings.ImGui.ImGuiTableFlags.RowBg, new Vector2(0, -1)))
        {
            Dalamud.Bindings.ImGui.ImGui.TableSetupColumn("Member", Dalamud.Bindings.ImGui.ImGuiTableColumnFlags.WidthFixed, 140f);
            Dalamud.Bindings.ImGui.ImGui.TableSetupColumn("Total", Dalamud.Bindings.ImGui.ImGuiTableColumnFlags.WidthFixed, 40f);
            for (int i = 0; i < 24; i++) Dalamud.Bindings.ImGui.ImGui.TableSetupColumn($"{i:D2}", Dalamud.Bindings.ImGui.ImGuiTableColumnFlags.WidthFixed, 18f);
            Dalamud.Bindings.ImGui.ImGui.TableHeadersRow();

            foreach (var activity in activityDb.Values.OrderByDescending(a => a.LastSeen))
            {
                Dalamud.Bindings.ImGui.ImGui.TableNextRow();
                Dalamud.Bindings.ImGui.ImGui.TableNextColumn();
                Dalamud.Bindings.ImGui.ImGui.TextUnformatted(activity.Name);

                Dalamud.Bindings.ImGui.ImGui.TableNextColumn();
                Dalamud.Bindings.ImGui.ImGui.TextColored(ImGuiColors.ParsedGold, $"{activity.LoginsPerHour.Sum()}");

                for (int h = 0; h < 24; h++)
                {
                    Dalamud.Bindings.ImGui.ImGui.TableNextColumn();
                    int val = activity.LoginsPerHour[h];
                    if (val > 0)
                    {
                        uint cellCol = val > 5 ? 0xFF00FF00 : (val > 2 ? 0xFF00AA00 : 0xFF005500);
                        Dalamud.Bindings.ImGui.ImGui.TableSetBgColor(Dalamud.Bindings.ImGui.ImGuiTableBgTarget.CellBg, cellCol);
                        Dalamud.Bindings.ImGui.ImGui.TextUnformatted($"{val}");
                    }
                }
            }
            Dalamud.Bindings.ImGui.ImGui.EndTable();
        }
    }

    private void RunScannerLogic()
    {
        if (!isScanning) return;
        int tickRate = isTurbo ? 150 : 400;
        if (scanTimer.ElapsedMilliseconds < tickRate) return;

        IntPtr addr = gameGui.GetAddonByName("FreeCompanyMember", 1);
        if (addr == IntPtr.Zero) return;

        AtkUnitBase* addon = (AtkUnitBase*)addr.ToPointer();
        var listNode = addon->GetNodeById(24);
        if (listNode != null) Discovery(listNode);

        bool hitPageTarget = memberList.Count >= (currentPage * TARGET_PER_PAGE);
        int stagLimit = isTurbo ? 25 : 50;
        bool stalledOnEnd = stagnationCount >= stagLimit;

        if (!isPausingForSwap && (hitPageTarget || stalledOnEnd) && swapCooldown.ElapsedMilliseconds > 5000 && allowPageSwap)
        {
            isPausingForSwap = true;
            pauseTimer.Restart();
        }
        else if (stalledOnEnd && !allowPageSwap) isScanning = false;

        if (isPausingForSwap)
        {
            long elapsed = pauseTimer.ElapsedMilliseconds;
            if (elapsed >= 400 && elapsed < 500) SendKey(VK_NUMPAD4);
            if (elapsed >= 1200 && elapsed < 1300) SendKey(VK_NUMPAD6);
            if (elapsed >= 2000) { currentPage++; stagnationCount = 0; lastMemberCount = memberList.Count; isPausingForSwap = false; swapCooldown.Restart(); }
        }
        else
        {
            if (memberList.Count > lastMemberCount) { lastMemberCount = memberList.Count; stagnationCount = 0; }
            else stagnationCount++;
            SendKey(VK_NUMPAD2);
        }
        scanTimer.Restart();
    }

    private void Discovery(AtkResNode* node)
    {
        if (node == null) return;
        if ((int)node->Type >= 1000)
        {
            uint id = node->NodeId;
            if (id == 5 || (id >= 51001 && id <= 51011)) { ProcessRow((AtkComponentNode*)node); return; }
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null && comp->Component->UldManager.NodeList != null)
                for (int i = 0; i < comp->Component->UldManager.NodeListCount; i++) Discovery(comp->Component->UldManager.NodeList[i]);
        }
        if (node->ChildNode != null) Discovery(node->ChildNode);
    }

    private void ProcessRow(AtkComponentNode* rowNode)
    {
        string name = ""; string loc = "";
        ScavengeText(rowNode->Component->UldManager.NodeList, rowNode->Component->UldManager.NodeListCount, ref name, ref loc);
        if (!string.IsNullOrEmpty(name) && nameRegex.IsMatch(name))
        {
            var existing = memberList.FirstOrDefault(m => m.Name == name);
            if (existing == null) memberList.Add(new FCMember { Name = name, Location = loc });
            else if (string.IsNullOrEmpty(existing.Location) && !string.IsNullOrEmpty(loc)) existing.Location = loc;
        }
    }

    private void ScavengeText(AtkResNode** nodeList, int count, ref string name, ref string loc)
    {
        if (nodeList == null) return;
        for (int i = 0; i < count; i++)
        {
            var node = nodeList[i];
            if (node == null) continue;
            if (node->Type == NodeType.Text)
            {
                var rawText = ((AtkTextNode*)node)->NodeText.ToString();
                var cleanText = Regex.Replace(rawText, @"[^\u0020-\u007E]", "").Trim();
                if (node->NodeId == 8) name = cleanText;
                else if (!string.IsNullOrEmpty(cleanText) && !rankBlacklist.Any(r => cleanText.Contains(r)))
                    if (cleanText.Length > 3 || timeRegex.IsMatch(cleanText)) loc = cleanText;
            }
            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null) ScavengeText(comp->Component->UldManager.NodeList, comp->Component->UldManager.NodeListCount, ref name, ref loc);
            }
        }
    }

    private void HandleSorting()
    {
        var sortSpecs = Dalamud.Bindings.ImGui.ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount > 0 && sortSpecs.SpecsDirty)
        {
            var spec = sortSpecs.Specs;
            if (spec.ColumnIndex == 0)
                memberList = spec.SortDirection == Dalamud.Bindings.ImGui.ImGuiSortDirection.Ascending ? memberList.OrderBy(m => m.Name).ToList() : memberList.OrderByDescending(m => m.Name).ToList();
            else
                memberList = spec.SortDirection == Dalamud.Bindings.ImGui.ImGuiSortDirection.Ascending ? memberList.OrderBy(m => m.Location).ToList() : memberList.OrderByDescending(m => m.Location).ToList();
            sortSpecs.SpecsDirty = false;
        }
    }

    private void SendKey(int keyCode)
    {
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        PostMessage(handle, WM_KEYDOWN, (IntPtr)keyCode, IntPtr.Zero);
        unchecked { PostMessage(handle, WM_KEYUP, (IntPtr)keyCode, (IntPtr)(long)0xC0000001); }
    }

    private void ExportToCsv()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FCMembers_Final.csv");
        var lines = memberList.Select(m => $"\"{m.Name}\",\"{m.Location}\"");
        File.WriteAllLines(path, lines);
    }

    private void SaveActivityData() { try { File.WriteAllText(configPath, JsonConvert.SerializeObject(activityDb)); } catch { } }
    private void LoadActivityData() { try { if (File.Exists(configPath)) activityDb = JsonConvert.DeserializeObject<Dictionary<string, MemberActivity>>(File.ReadAllText(configPath)) ?? new(); } catch { } }

    public class FCMember { public string Name { get; set; } = ""; public string Location { get; set; } = ""; }
    public class MemberActivity { public string Name { get; set; } = ""; public int[] LoginsPerHour { get; set; } = new int[24]; public DateTime LastSeen { get; set; } }
}
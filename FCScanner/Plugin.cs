using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FCScanner.Windows;

namespace FCScanner
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Keepers Toolkit";
        private const string CommandName = "/fcscan";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        private IGameGui GameGui { get; init; }
        private IChatGui ChatGui { get; init; }

        public WindowSystem WindowSystem = new("KeepersToolkit");
        private MainWindow MainWindow { get; init; }

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IGameGui gameGui,
            IChatGui chatGui)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.GameGui = gameGui;
            this.ChatGui = chatGui;

            this.MainWindow = new MainWindow(
                this,
                this.GameGui,
                this.ChatGui,
                this.PluginInterface.GetPluginConfigDirectory()
            );

            WindowSystem.AddWindow(MainWindow);

            this.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the Keepers Toolkit for FC management."
            });

            this.PluginInterface.UiBuilder.Draw += DrawUI;

            // FIX FOR VALIDATION ISSUE: Registers the main entry point UI callback
            this.PluginInterface.UiBuilder.OpenMainUi += () => MainWindow.IsOpen = true;
            this.PluginInterface.UiBuilder.OpenConfigUi += () => MainWindow.IsOpen = true;
        }

        public void Dispose()
        {
            this.WindowSystem.RemoveAllWindows();
            this.MainWindow.Dispose();
            this.CommandManager.RemoveHandler(CommandName);
        }

        private void OnCommand(string command, string args) => MainWindow.IsOpen = true;
        private void DrawUI() => this.WindowSystem.Draw();
    }
}
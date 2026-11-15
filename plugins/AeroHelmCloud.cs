// VIHANGA Cloud — isolated Mission Planner plugin (first integration milestone).
// No MAVLink transport is installed or replaced by this plugin.
// Cloud "Connect" is an operator telemetry VIEW session, never flight authority.
// Requires HTTPS except loopback development. Credentials/tokens stay in memory.
// Mission Planner retains its original USB/TCP/UDP connection functionality.
//loadassembly: System.Web.Extensions
// Copyright (C) AeroHelm contributors. GPL-3.0-or-later (see repository COPYING.txt).

extern alias FrameworkDrawing;

using MissionPlanner;
using MissionPlanner.Plugin;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    public sealed class AeroHelmCloudPlugin : MissionPlanner.Plugin.Plugin
    {
        private ToolStripButton _paths;
        private ToolStripDropDownButton _operations;
        private ToolStripDropDownButton _workspaces;
        private ToolStripButton _camera;
        private readonly Dictionary<ToolStripItem, bool> _nativeAvailability =
            new Dictionary<ToolStripItem, bool>();
        internal static AeroHelmReceiveOnlyBridge ActiveNativeBridge;
        private readonly System.Windows.Forms.Timer _cloudLinkWatchdog =
            new System.Windows.Forms.Timer();
        private bool _cloudProbeBusy;

        public override string Name { get { return "Vihanga Ground Station"; } }
        public override string Version { get { return "0.3.0-rc1"; } }
        public override string Author { get { return "AeroHelm Contributors"; } }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            var main = MainV2.instance;
            var menu = main.MainMenu;
            int plannerIndex = menu.Items.IndexOf(main.MenuFlightPlanner);
            int at = plannerIndex >= 0 ? plannerIndex + 1 : 0;

            _paths = NewToolbarButton("Connections", "PATHS",
                "Choose private cloud telemetry or the direct USB/radio link");
            _paths.Click += delegate
            {
                using (var picker = new AeroHelmLinkSelector())
                {
                    if (picker.ShowDialog(main) == DialogResult.OK)
                        AeroHelmLinkNavigation.Open(main, picker.Selection, null);
                }
            };
            menu.Items.Insert(at++, _paths);

            _operations = NewToolbarMenu("Operations", "OPS",
                "Flight records, aircraft status and fleet");
            _operations.DropDownItems.Add("Flight records and status", null,
                (sender, e) => ShowWorkspace(new AeroHelmOpsWindow()));
            _operations.DropDownItems.Add("Fleet", null, (sender, e) =>
            {
                if (VihangaLinkLoginWindow.EnsureSignedIn(main))
                    ShowWorkspace(new AeroHelmReadOnlyPage("FLEET"));
            });
            menu.Items.Insert(at++, _operations);

            _workspaces = NewToolbarMenu("Tools", "SETTINGS", "Diagnostics and settings");
            _workspaces.DropDownItems.Add("Link diagnostics", null,
                (sender, e) => ShowWorkspace(new AeroHelmLinkWindow()));
            _workspaces.DropDownItems.Add("Vihanga settings", null,
                (sender, e) => ShowWorkspace(new VihangaSettingsWindow()));

            bool reducedToolbar = !string.Equals(Environment.GetEnvironmentVariable(
                "AEROHELM_REDUCED_TOOLBAR"), "0", StringComparison.Ordinal);
            if (reducedToolbar)
            {
                foreach (var item in new[] { main.MenuInitConfig, main.MenuConfigTune,
                                             main.MenuSimulation, main.MenuHelp })
                {
                    _nativeAvailability.Add(item, item.Available);
                    item.Available = false;
                }
                var maintenance = new ToolStripMenuItem("Mission Planner maintenance")
                {
                    CheckOnClick = true,
                    ToolTipText = "Show Mission Planner setup, configuration, simulation and help"
                };
                maintenance.CheckedChanged += (sender, e) =>
                {
                    foreach (var entry in _nativeAvailability)
                        entry.Key.Available = maintenance.Checked && entry.Value;
                };
                _workspaces.DropDownItems.Add(maintenance);
            }

            if (string.Equals(Environment.GetEnvironmentVariable(
                    "VIHANGA_ADVANCED_TOOLS"), "1", StringComparison.Ordinal))
            {
                _workspaces.DropDownItems.Add(new ToolStripSeparator());
                var planPreview = new ToolStripMenuItem("Simulator mission preview");
                planPreview.Visible = VihangaLinkSession.UsesDedicatedRelay;
                planPreview.Click += (sender, e) =>
                {
                    if (VihangaLinkLoginWindow.EnsureSignedIn(main))
                        ShowWorkspace(new VihangaMissionPreviewWindow());
                };
                _workspaces.DropDownItems.Add(planPreview);
                _workspaces.DropDownItems.Add("Attitude monitor", null,
                    (sender, e) =>
                    {
                        if (VihangaLinkLoginWindow.EnsureSignedIn(main))
                            ShowWorkspace(new AeroHelmCloudAttitudeWindow());
                    });
                _workspaces.DropDownItems.Add("Operations tools", null,
                    (sender, e) => ShowWorkspace(new AeroHelmOpsWindow(true)));
                _workspaces.DropDownItems.Add("Fleet diagnostics", null,
                    (sender, e) => ShowWorkspace(new AeroHelmIntelWindow()));
            }
            menu.Items.Insert(at++, _workspaces);
            _camera = NewToolbarButton("Camera", "VISION", "Open aircraft cameras");
            _camera.Click += (sender, e) => ShowWorkspace(new AeroHelmVisionWindow());
            menu.Items.Insert(at, _camera);

            _cloudLinkWatchdog.Interval = 5000;
            _cloudLinkWatchdog.Tick += async (sender, args) => await CheckCloudLinkAsync();
            _cloudLinkWatchdog.Start();
            _ = CheckCloudLinkAsync();
            // The native loopback bridge consumes the already authenticated
            // in-memory startup session: one cloud login, no Python terminal.
            // Never run both the native and legacy Python bridges on port 5760.
            if (string.Equals(Environment.GetEnvironmentVariable(
                    "AEROHELM_INTERNAL_TCP_BRIDGE"), "1", StringComparison.Ordinal))
            {
                ActiveNativeBridge = new AeroHelmReceiveOnlyBridge();
                ActiveNativeBridge.Start(); // Fail closed if login/port unavailable.
            }

            // Mission Planner remains the operator's primary workspace.
            return true;
        }

        private async Task CheckCloudLinkAsync()
        {
            if (_cloudProbeBusy || _paths == null || _paths.IsDisposed) return;
            _cloudProbeBusy = true;
            try
            {
                // A dedicated private telemetry relay owns its own token and
                // receiver. Never infer its HUD health from Oracle M1 status.
                if (VihangaLinkSession.UsesDedicatedRelay)
                {
                    var active = ActiveNativeBridge;
                    bool hbLive = active != null && active.NativeHeartbeatFresh;
                    bool attLive = active != null && active.NativeAttitudeFresh;
                    _paths.ForeColor = hbLive && attLive ?
                        FrameworkDrawing::System.Drawing.Color.FromArgb(195,228,195) :
                        FrameworkDrawing::System.Drawing.Color.FromArgb(242,198,139);
                    _paths.ToolTipText = hbLive && attLive ?
                        "Private relay: live Pixhawk heartbeat and attitude." :
                        hbLive ? "Private relay: heartbeat live; attitude missing." :
                        "Private relay: not receiving verified native MAVLink.";
                    return;
                }
                // Local USB/telemetry-radio recovery is usable without
                // Oracle, cloud credentials or Kerberos. Never treat the
                // absence of a cloud token as a native aircraft fault.
                if(string.IsNullOrWhiteSpace(Program.AeroHelmOperatorToken) ||
                   DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
                {
                    _paths.ToolTipText = "Cloud sign-in required. The direct USB/radio link is available independently.";
                    _paths.ForeColor = FrameworkDrawing::System.Drawing.Color.FromArgb(
                        242,198,139);
                    return;
                }
                string raw = await Task.Run(()=>OperationsApi.Request(
                    "GET","api/v1/mavlink/health"));
                if(_paths == null || _paths.IsDisposed) return;
                var result = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string,object>>(raw);
                object online, fresh, hb, att;
                bool link = result.TryGetValue("device_online",out online) &&
                            online is bool && (bool)online &&
                            result.TryGetValue("fresh",out fresh) &&
                            fresh is bool && (bool)fresh;
                bool hbKnown = result.TryGetValue("native_hud_heartbeat_ready",out hb)
                               && hb is bool;
                bool attKnown = result.TryGetValue("attitude_stream_active",out att)
                                && att is bool;
                bool ready = link && hbKnown && (bool)hb &&
                             attKnown && (bool)att;
                _paths.ForeColor = ready ?
                    FrameworkDrawing::System.Drawing.Color.FromArgb(195,228,195) :
                    FrameworkDrawing::System.Drawing.Color.FromArgb(242,198,139);
                _paths.ToolTipText = ready ?
                    "Cloud: heartbeat and attitude verified; choose link." :
                    !link ?
                    "Cloud: offline or stale; direct link requires pilot selection." :
                    !hbKnown || !attKnown ?
                    "Cloud: frames received; heartbeat and attitude not verified." :
                    "Cloud: heartbeat or attitude missing.";
            }
            catch (Exception)
            {
                if(_paths != null && !_paths.IsDisposed)
                {
                    _paths.ForeColor=FrameworkDrawing::System.Drawing.Color.FromArgb(
                        244,177,150);
                    _paths.ToolTipText="Cloud status unavailable; direct connection remains independent.";
                }
            }
            finally { _cloudProbeBusy = false; }
        }

        private static void ShowWorkspace(Form window)
        {
            using (window)
                window.ShowDialog(MainV2.instance);
        }

        private static ToolStripButton NewToolbarButton(string label, string icon, string tip)
        {
            return new ToolStripButton(label)
            {
                Name = "AeroHelm" + label + "Button",
                ToolTipText = tip,
                Image = AeroHelmIconPainter.Make(icon,20),
                ImageScaling = ToolStripItemImageScaling.None,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ForeColor = FrameworkDrawing::System.Drawing.Color.White,
                Margin = new Padding(4, 0, 4, 0),
                AutoSize = true
            };
        }

        private static ToolStripDropDownButton NewToolbarMenu(string label, string icon, string tip)
        {
            return new ToolStripDropDownButton(label)
            {
                Name = "Vihanga" + label + "Button",
                ToolTipText = tip,
                Image = AeroHelmIconPainter.Make(icon,20),
                ImageScaling = ToolStripItemImageScaling.None,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ForeColor = FrameworkDrawing::System.Drawing.Color.White,
                Margin = new Padding(4, 0, 4, 0),
                AutoSize = true
            };
        }

        public override bool Exit()
        {
            _cloudLinkWatchdog.Stop();
            _cloudLinkWatchdog.Dispose();
            if (ActiveNativeBridge != null)
            {
                ActiveNativeBridge.Dispose();
                ActiveNativeBridge = null;
            }
            var main = MainV2.instance;
            if (main != null && !main.IsDisposed)
            {
                foreach (var entry in _nativeAvailability)
                    entry.Key.Available = entry.Value;
                foreach (var item in new ToolStripItem[] { _paths, _operations, _workspaces, _camera })
                    if (item != null) main.MainMenu.Items.Remove(item);
            }
            _nativeAvailability.Clear();
            foreach (ToolStripItem item in new ToolStripItem[] { _paths, _operations, _workspaces, _camera })
            {
                if (item == null) continue;
                if (item.Image != null)
                {
                    var owned = item.Image;
                    item.Image = null;
                    owned.Dispose();
                }
                item.Dispose();
            }
            _paths = null;
            _operations = null;
            _workspaces = null;
            _camera = null;
            return true;
        }
    }

}

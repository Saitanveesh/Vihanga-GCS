// AeroHelm supervised maintenance / simulated autonomy UI.
// Authenticated, no local UART, no direct MAVLink command serialization.
// Hardware mission execution is not available in this development stage.
// Copyright AeroHelm contributors, GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal static class OperationsApi
    {
        internal static string Request(string method,string path,object body=null)
        {
            if(string.IsNullOrEmpty(Program.AeroHelmOperatorToken) ||
               DateTime.UtcNow>=Program.AeroHelmTokenExpiresUtc)
                throw new InvalidOperationException(
                    "Cloud login required or expired. Sign in again via CLOUD.");
            Uri origin = AeroHelmWorkspaceSettings.CheckedPrivateUrl(Program.AeroHelmCloudOrigin);
            var url=new Uri(new Uri(origin.GetLeftPart(UriPartial.Authority)+"/"),
                            path.TrimStart('/'));
            var req=(HttpWebRequest)WebRequest.Create(url);
            req.Method=method;
            req.Timeout=12000;req.ReadWriteTimeout=12000;
            req.AllowAutoRedirect=false;
            req.Accept="application/json";
            req.Headers[HttpRequestHeader.Authorization]="Bearer "+Program.AeroHelmOperatorToken;
            if(body!=null)
            {
                byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                req.ContentType="application/json";
                req.ContentLength=bytes.Length;
                using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
            }
            using(var response=(HttpWebResponse)req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }

    internal static class AeroHelmBackendProfile
    {
        private static bool Absent(WebException error)
        {
            var response=error.Response as HttpWebResponse;
            if(response==null || response.StatusCode!=HttpStatusCode.NotFound)
                return false;
            response.Dispose();
            return true;
        }

        // Same signed-in HTTPS origin only. These are GET-only discovery
        // fallbacks; never forward the M1 JWT to a different R2 host.
        internal static string ReadFleet(string primary)
        {
            if(primary!="api/v2/fleet")
                throw new ArgumentException("Unsupported fleet discovery route.");
            try { return "V2 FLEET VIEW\r\n"+OperationsApi.Request("GET",primary); }
            catch(WebException ex)
            {
                if(!Absent(ex)) throw;
            }
            try {
                return "R2 DEVICE-REPORTED FLEET (not flight clearance)\r\n"+
                    OperationsApi.Request("GET","api/v1/fleet/status");
            }
            catch(WebException ex) { if(!Absent(ex)) throw; }
            return "M1 REGISTERED AIRCRAFT ONLY; NO V2 SECURITY, ANALYTICS OR HISTORY\r\n"+
                OperationsApi.Request("GET","api/v1/vehicles");
        }

        internal static string ReadFallbackCapabilities()
        {
            try {
                return "R2 DECLARED CAPABILITIES (requires matching identity)\r\n"+
                    OperationsApi.Request("GET","api/v1/capabilities");
            }
            catch(WebException ex) { if(!Absent(ex)) throw; }
            return "M1 TELEMETRY HEALTH ONLY; OPERATIONS/RESEARCH NOT DEPLOYED\r\n"+
                OperationsApi.Request("GET","api/v1/mavlink/health");
        }

        internal static bool IsRouteAbsent(WebException error) { return Absent(error); }
    }

    internal sealed class AeroHelmOpsWindow : Form
    {
        private readonly TextBox missionText = new TextBox();
        private readonly TextBox missionId = new TextBox();
        private readonly TextBox leaseId = new TextBox();
        private readonly ComboBox kind = new ComboBox();
        private readonly CheckBox onsite = new CheckBox();
        private readonly CheckBox props = new CheckBox();
        private readonly TextBox status = new TextBox();
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        private readonly Button start = new Button();
        private readonly Button abort = new Button();
        private readonly Button cal = new Button();
        private readonly System.Windows.Forms.Timer leaseRenewal = new System.Windows.Forms.Timer();
        private string calibrationRequestId = "";
        private bool busy;
        private bool renewing;

        public sealed class FlightRecord
        {
            public int Version { get; set; }
            public string Aircraft { get; set; }
            public string Mission { get; set; }
            public string Notes { get; set; }
            public string SavedUtc { get; set; }
            public string[] ConfirmedPreFlight { get; set; }
            public string[] ConfirmedPostFlight { get; set; }
        }

        private static TabPage CreateFlightChecklist()
        {
            var page = new TabPage("Flight record");
            var layout = new FlowLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false
            };
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "Aircraft ID", AutoSize = true });
            var aircraft = new TextBox { Width = 460, MaxLength = 120 };
            layout.Controls.Add(aircraft);
            layout.Controls.Add(new Label { Text = "Mission reference", AutoSize = true });
            var mission = new TextBox { Width = 460, MaxLength = 120 };
            layout.Controls.Add(mission);

            var checks = new Dictionary<string, CheckedListBox>();
            AddChecklist(layout, checks, "Pre-flight", new[] {
                "Aircraft and payload inspected",
                "Battery condition and charge checked",
                "Mission area, route and weather reviewed",
                "Pilot control link and backup checked",
                "Navigation status and home position confirmed",
                "Recovery procedure reviewed with the crew"
            });
            AddChecklist(layout, checks, "Post-flight", new[] {
                "Aircraft disarmed and secured",
                "Aircraft and payload inspected",
                "Battery condition recorded",
                "Flight logs and issues recorded"
            });

            layout.Controls.Add(new Label { Text = "Pilot notes", AutoSize = true });
            var notes = new TextBox {
                Width = 460, Height = 80, MaxLength = 4000, Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };
            layout.Controls.Add(notes);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            layout.Controls.Add(actions);
            var open = new Button { Text = "Open record", AutoSize = true };
            var save = new Button { Text = "Save record", AutoSize = true };
            var result = new Label { AutoSize = true };
            actions.Controls.Add(open);
            actions.Controls.Add(save);
            layout.Controls.Add(result);
            const string filter = "Vihanga flight record (*.json)|*.json";

            open.Click += (sender, args) => {
                using (var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true })
                {
                    if (dialog.ShowDialog(page) != DialogResult.OK)
                        return;

                    if (MessageBox.Show(page, "Replace the current form with the selected record?",
                        "Open record", MessageBoxButtons.OKCancel) != DialogResult.OK)
                        return;

                    try
                    {
                        var record = ReadFlightRecord(dialog.FileName);
                        aircraft.Text = record.Aircraft;
                        mission.Text = record.Mission;
                        notes.Text = record.Notes;
                        ApplyChecks(checks["Pre-flight"], record.ConfirmedPreFlight);
                        ApplyChecks(checks["Post-flight"], record.ConfirmedPostFlight);
                        result.Text = "Opened " + Path.GetFileName(dialog.FileName);
                    }
                    catch (Exception error) when (IsRecordError(error))
                    {
                        result.Text = "Could not open the record.";
                        MessageBox.Show(page, error.Message);
                    }
                }
            };

            save.Click += (sender, args) => {
                if (string.IsNullOrWhiteSpace(aircraft.Text) || string.IsNullOrWhiteSpace(mission.Text))
                {
                    result.Text = "Enter both the aircraft ID and mission reference.";
                    return;
                }

                using (var dialog = new SaveFileDialog {
                    Filter = filter, FileName = "Vihanga-flight-record.json", DefaultExt = "json",
                    AddExtension = true, OverwritePrompt = true
                })
                {
                    if (dialog.ShowDialog(page) != DialogResult.OK)
                        return;

                    var record = new FlightRecord {
                        Version = 1, Aircraft = aircraft.Text.Trim(), Mission = mission.Text.Trim(),
                        Notes = notes.Text, SavedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ConfirmedPreFlight = ReadChecks(checks["Pre-flight"]),
                        ConfirmedPostFlight = ReadChecks(checks["Post-flight"])
                    };

                    try
                    {
                        WriteFlightRecord(dialog.FileName, record);
                        result.Text = "Saved " + Path.GetFileName(dialog.FileName);
                    }
                    catch (Exception error) when (IsRecordError(error))
                    {
                        result.Text = "Could not save the record.";
                        MessageBox.Show(page, error.Message);
                    }
                }
            };
            return page;
        }

        private static string[] ReadChecks(CheckedListBox list)
        {
            var result = new List<string>();
            foreach (string item in list.CheckedItems)
                result.Add(item);
            return result.ToArray();
        }

        private static void ApplyChecks(CheckedListBox list, string[] checkedItems)
        {
            var confirmed = new HashSet<string>(checkedItems, StringComparer.Ordinal);
            for (var index = 0; index < list.Items.Count; index++)
                list.SetItemChecked(index, confirmed.Contains((string)list.Items[index]));
        }

        private static FlightRecord ReadFlightRecord(string path)
        {
            const int maxCharacters = 65536;
            var buffer = new char[maxCharacters + 1];
            int length;
            using (var stream = new StreamReader(path, Encoding.UTF8))
                length = stream.ReadBlock(buffer, 0, buffer.Length);

            if (length > maxCharacters)
                throw new InvalidDataException("The file is too large for a flight record.");

            var serializer = new JavaScriptSerializer { MaxJsonLength = maxCharacters, RecursionLimit = 12 };
            var record = serializer.Deserialize<FlightRecord>(new string(buffer, 0, length));
            if (record == null || record.Version != 1 ||
                string.IsNullOrWhiteSpace(record.Aircraft) || record.Aircraft.Length > 120 ||
                string.IsNullOrWhiteSpace(record.Mission) || record.Mission.Length > 120 ||
                record.Notes == null || record.Notes.Length > 4000 ||
                record.ConfirmedPreFlight == null || record.ConfirmedPostFlight == null)
                throw new InvalidDataException("The file does not contain a supported flight record.");
            return record;
        }

        private static void WriteFlightRecord(string path, FlightRecord record)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(record), Encoding.UTF8);
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static bool IsRecordError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is InvalidOperationException;
        }

        private static TabPage CreateFlightStatus()
        {
            var page = new TabPage("Aircraft status");
            var layout = new FlowLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false
            };
            page.Controls.Add(layout);
            var refresh = new Button { Text = "Refresh Telemetry", AutoSize = true };
            var notice = new Label { Text = "Sign in to view cloud telemetry.", AutoSize = true };
            var readings = new TableLayoutPanel {
                AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8)
            };
            readings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            readings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var fields = new[] {
                "Aircraft", "Cloud Link", "Telemetry", "Heartbeat", "Attitude", "Mission Item", "Last Telemetry"
            };
            var values = new Label[fields.Length];
            for (var row = 0; row < fields.Length; row++)
            {
                readings.Controls.Add(new Label {
                    Text = fields[row], AutoSize = true, Margin = new Padding(0, 4, 24, 4)
                }, 0, row);
                values[row] = new Label { Text = "Unavailable", AutoSize = true };
                readings.Controls.Add(values[row], 1, row);
            }
            layout.Controls.Add(refresh);
            layout.Controls.Add(notice);
            layout.Controls.Add(readings);

            var poll = new Timer { Interval = 3000 };
            void ClearTelemetry(string message)
            {
                notice.Text = message;
                foreach (var value in values)
                    value.Text = "Unavailable";
            }

            async Task FetchTelemetry()
            {
                if (!refresh.Enabled || page.IsDisposed)
                    return;
                refresh.Enabled = false;
                try
                {
                    var dedicated = VihangaLinkSession.UsesDedicatedRelay;
                    var token = VihangaLinkSession.AccessToken;
                    if (string.IsNullOrEmpty(token) || DateTime.UtcNow >= VihangaLinkSession.ExpiresUtc)
                    {
                        ClearTelemetry("Sign in to view cloud telemetry.");
                        return;
                    }
                    var origin = VihangaLinkSession.CloudOrigin;
                    var snapshot = await Task.Run(() => {
                        var response = dedicated
                            ? VihangaLinkSession.ReadRelayHealth()
                            : OperationsApi.Request("GET", "api/v1/mavlink/health");
                        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response);
                    });
                    if (page.IsDisposed)
                        return;
                    if (dedicated != VihangaLinkSession.UsesDedicatedRelay ||
                        token != VihangaLinkSession.AccessToken || origin != VihangaLinkSession.CloudOrigin ||
                        DateTime.UtcNow >= VihangaLinkSession.ExpiresUtc)
                    {
                        ClearTelemetry("Cloud session changed.");
                        return;
                    }
                    if (snapshot == null || !snapshot.TryGetValue("vehicle_id", out var aircraft) ||
                        string.IsNullOrWhiteSpace(aircraft as string))
                        throw new InvalidDataException("The relay did not identify an aircraft.");

                    values[0].Text = (string)aircraft;
                    values[1].Text = DescribeFlag(snapshot, "device_online", "Connected", "Disconnected");
                    values[2].Text = DescribeFlag(snapshot, "fresh", "Fresh", "Stale");
                    values[3].Text = DescribeFlag(snapshot, "native_hud_heartbeat_ready", "Fresh", "Stale");
                    values[4].Text = DescribeFlag(snapshot, "attitude_stream_active", "Fresh", "Stale");
                    values[5].Text = snapshot.TryGetValue("mission_sequence", out var sequence) &&
                        sequence != null ? Convert.ToString(sequence, CultureInfo.InvariantCulture) : "Unavailable";
                    if ((!snapshot.TryGetValue("last_observed_frame_age_ms", out var age) || age == null) &&
                        !snapshot.TryGetValue("frame_age_ms", out age))
                        age = null;
                    values[6].Text = age == null ? "Unavailable" :
                        (Convert.ToDouble(age, CultureInfo.InvariantCulture) / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s ago";
                    notice.Text = "Updated at " + DateTime.Now.ToString("T", CultureInfo.CurrentCulture);
                }
                catch (Exception error) when (error is WebException || IsRecordError(error) ||
                    error is FormatException || error is InvalidCastException || error is OverflowException)
                {
                    if (!page.IsDisposed)
                        ClearTelemetry("Cloud telemetry unavailable.");
                }
                finally
                {
                    if (!page.IsDisposed)
                        refresh.Enabled = true;
                }
            }
            refresh.Click += async (sender, args) => await FetchTelemetry();
            poll.Tick += async (sender, args) => await FetchTelemetry();
            page.VisibleChanged += async (sender, args) => {
                if (page.Visible)
                {
                    poll.Start();
                    await FetchTelemetry();
                }
                else
                    poll.Stop();
            };
            page.Disposed += (sender, args) => poll.Dispose();
            return page;
        }

        private static string DescribeFlag(Dictionary<string, object> values, string key, string yes, string no)
        {
            if (!values.TryGetValue(key, out var value) || !(value is bool flag))
                return "Unavailable";
            return flag ? yes : no;
        }

        private static void AddChecklist(FlowLayoutPanel layout,
            Dictionary<string, CheckedListBox> checks, string title, string[] entries)
        {
            layout.Controls.Add(new Label { Text = title, AutoSize = true });
            var list = new CheckedListBox { Width = 460, CheckOnClick = true };
            list.Items.AddRange(entries);
            list.Height = list.ItemHeight * entries.Length + SystemInformation.BorderSize.Height * 2 + 2;
            checks.Add(title, list);
            layout.Controls.Add(list);
        }

        public AeroHelmOpsWindow(bool toolsOnly = false)
        {
            Text = toolsOnly ? "Operations Tools" : "Operations";
            StartPosition=FormStartPosition.CenterParent;
            Width=860;Height=730;
            MinimumSize=new Drawing.Size(560,460);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            Controls.Add(tabs);
            BackColor=Drawing.Color.FromArgb(8,8,8);
            ForeColor=Drawing.Color.White;
            AutoScaleMode=AutoScaleMode.Dpi;
            KeyPreview=true;
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
            if (!toolsOnly)
            {
                tabs.TabPages.Add(CreateFlightStatus());
                tabs.TabPages.Add(CreateFlightChecklist());
                ApplyDarkTheme(this);
                return;
            }

            var simulation=new TabPage("Simulator");
            var maintenance=new TabPage("Bench maintenance");
            var policy=new TabPage("Cloud Status");
            tabs.TabPages.Add(simulation);tabs.TabPages.Add(maintenance);
            tabs.TabPages.Add(policy);
            status.Dock=DockStyle.Bottom;status.Height=140;status.Multiline=true;
            status.ScrollBars=ScrollBars.Vertical;status.ReadOnly=true;
            status.Text="Sign in to check cloud status.";
            Controls.Add(status);
            leaseRenewal.Interval=10000; // 30-second lease; renew every 10 seconds
            leaseRenewal.Tick+=async(s,e)=>await RenewSimulationLease();

            var sim=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),
                AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            simulation.Controls.Add(sim);
            sim.Controls.Add(new Label{Text="Paste 2–80 waypoints in this order: latitude, longitude, relative altitude (m).",
                Width=690,Height=25});
            missionText.Multiline=true;missionText.Width=680;missionText.Height=150;
            missionText.ScrollBars=ScrollBars.Vertical;
            sim.Controls.Add(missionText);
            var load=new Button{Text="Load WP File",Width=255};
            load.Click+=(s,e)=>LoadSurveyFile();
            sim.Controls.Add(load);
            var draft=new Button{Text="Create Mission Draft",Width=255};
            draft.Click+=async(s,e)=>await CreateDraft();
            sim.Controls.Add(draft);
            sim.Controls.Add(new Label{Text="Mission ID",Width=680});
            missionId.Width=680;missionId.ReadOnly=true;sim.Controls.Add(missionId);
            var lease=new Button{Text="Request Simulator Control",Width=255};
            lease.Click+=async(s,e)=>await AcquireLease();
            sim.Controls.Add(lease);
            sim.Controls.Add(new Label{Text="Control Lease ID",Width=680});
            leaseId.ReadOnly=true;leaseId.Width=680;sim.Controls.Add(leaseId);
            start.Text="Start Simulation";start.Width=255;
            abort.Text="Abort Simulation";abort.Width=255;
            start.Click+=async(s,e)=>await MissionAction("start");
            abort.Click+=async(s,e)=>await MissionAction("abort");
            sim.Controls.Add(start);sim.Controls.Add(abort);
            var pause = new Button{Text="Pause Simulation",Width=255};
            pause.Click+=async(s,e)=>await MissionAction("pause");
            sim.Controls.Add(pause);
            var resume = new Button{Text="Resume Simulation",Width=255};
            resume.Click+=async(s,e)=>await MissionAction("resume");
            sim.Controls.Add(resume);
            var refresh = new Button{Text="Refresh mission progress",Width=255};
            refresh.Click+=async(s,e)=>await RefreshMission();
            sim.Controls.Add(refresh);
            sim.Controls.Add(new Label{Width=680,Height=55,
                Text="Connect to a server with simulation enabled."});

            var maint=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),
                AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            maintenance.Controls.Add(maint);
            maint.Controls.Add(new Label{Width=680,Height=65,
                Text="Disarm the Pixhawk and remove all propellers. A technician must approve the calibration on the Pi within 45 seconds."});
            kind.DropDownStyle=ComboBoxStyle.DropDownList;
            kind.Items.AddRange(new object[]{"GYRO","BAROMETER","ACCEL_SIMPLE"});
            kind.SelectedIndex=0;kind.Width=250;maint.Controls.Add(kind);
            onsite.Text="A technician is physically present at the aircraft";
            onsite.Width=500;maint.Controls.Add(onsite);
            props.Text="I confirm all propellers are removed and Pixhawk is disarmed";
            props.Width=550;maint.Controls.Add(props);
            cal.Text="Request Calibration";cal.Width=260;
            cal.Click+=async(s,e)=>await Calibrate();
            maint.Controls.Add(cal);
            var checkCal = new Button{Text="Check Calibration",Width=260};
            checkCal.Click+=async(s,e)=>await RefreshCalibration();
            maint.Controls.Add(checkCal);
            maint.Controls.Add(new Label{Width=650,Height=90,
                Text="To approve the request, run this on the aircraft’s Pi:\n" +
                    "agent/.venv/bin/python -m agent.supervised_calibration <request-uuid>\n" +
                    "Use the request ID shown below."});

            // Read-only policy overview is suitable for checking the cloud
            // before any later Pi/hardware acceptance. Never grants a lease.
            var policyPage=new FlowLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(12),
                AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false
            };
            policy.Controls.Add(policyPage);
            policyPage.Controls.Add(new Label {
                Text="Cloud status is read-only.",
                Width=680,Height=44
            });
            var capabilityReport=new TextBox {
                Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,
                Width=680,Height=260,
                Text="No cloud status checked yet."
            };
            var refreshPolicy=new Button {Text="Refresh Cloud Status",Width=310};
            refreshPolicy.Click+=async(s,e)=>{
                refreshPolicy.Enabled=false;
                capabilityReport.Text="Checking cloud status...";
                try
                {
                    capabilityReport.Text=await Api("GET","api/v1/operations/capabilities");
                }
                catch(WebException ex)
                {
                    if(AeroHelmBackendProfile.IsRouteAbsent(ex))
                    {
                        try {
                            var fallback=await Task.Run(
                                ()=>AeroHelmBackendProfile.ReadFallbackCapabilities());
                            if(!IsDisposed && !Disposing)
                                capabilityReport.Text=fallback;
                        }
                        catch(Exception error)
                        {
                            if(!IsDisposed && !Disposing)
                                capabilityReport.Text="Deployment profile unavailable: "+Error(error);
                        }
                    }
                    else if(!IsDisposed && !Disposing)
                        capabilityReport.Text="Policy unavailable: "+Error(ex);
                }
                catch(Exception ex){if(!IsDisposed && !Disposing)capabilityReport.Text="Policy unavailable: "+Error(ex);}
                finally {if(!IsDisposed&&!Disposing)refreshPolicy.Enabled=true;}
            };
            policyPage.Controls.Add(refreshPolicy);
            policyPage.Controls.Add(capabilityReport);
            // The newer fleet service has a different, virtual-only API.
            // These GET actions do not create leases, missions or device jobs.
            var cloudR2Capabilities=new Button {
                Text="Fleet Service Status",Width=350
            };
            cloudR2Capabilities.Click+=async(s,e)=>{
                cloudR2Capabilities.Enabled=false;
                try
                {
                    capabilityReport.Text="Fleet service capabilities:"+
                        Environment.NewLine+
                        await Api("GET","api/v1/capabilities")+
                        Environment.NewLine+
                        "Physical flight control is disabled.";
                }
                catch(Exception ex)
                {
                    capabilityReport.Text="Fleet service unavailable: "+Error(ex);
                }
                finally { if(!IsDisposed)cloudR2Capabilities.Enabled=true; }
            };
            policyPage.Controls.Add(cloudR2Capabilities);
            var cloudR2Missions=new Button {
                Text="List Simulator Missions",Width=350
            };
            cloudR2Missions.Click+=async(s,e)=>{
                cloudR2Missions.Enabled=false;
                try
                {
                    capabilityReport.Text="Simulator missions:"+
                        Environment.NewLine+
                        await Api("GET","api/v1/missions?limit=50");
                }
                catch(Exception ex)
                {
                    capabilityReport.Text="Simulator missions unavailable: "+Error(ex);
                }
                finally { if(!IsDisposed)cloudR2Missions.Enabled=true; }
            };
            policyPage.Controls.Add(cloudR2Missions);
            policyPage.Controls.Add(new Label {
                Width=680,Height=80,
                Text="Cloud missions run in the simulator. Bench calibration requires approval on the aircraft’s Pi."
            });

            foreach(var panel in new[]{sim,maint,policyPage})
            {
                var home=new Button {Text="Close",Width=300,Height=42};
                home.Click+=(s,e)=>Close();
                panel.Controls.Add(home);
                panel.Controls.SetChildIndex(home,0);
            }
            ApplyDarkTheme(this);
            VihangaAdaptivePanel.Attach(this,sim,maint,policyPage);
        }

        private static void ApplyDarkTheme(Control parent)
        {
            foreach(Control child in parent.Controls)
            {
                if(child is TabPage || child is FlowLayoutPanel || child is TabControl)
                {
                    child.BackColor=Drawing.Color.FromArgb(18,18,18);
                    child.ForeColor=Drawing.Color.White;
                }
                if(child is Button)
                {
                    var button=(Button)child;
                    button.Height=Math.Max(40,button.Height);
                    button.FlatStyle=FlatStyle.Flat;
                    button.FlatAppearance.BorderSize=0;
                    button.BackColor=Drawing.Color.FromArgb(39,39,39);
                    button.ForeColor=Drawing.Color.White;
                    button.UseVisualStyleBackColor=false;
                }
                else if(child is TextBox || child is ComboBox)
                {
                    child.BackColor=Drawing.Color.FromArgb(32,32,32);
                    child.ForeColor=Drawing.Color.White;
                }
                else if(child is Label || child is CheckBox)
                {
                    child.ForeColor=Drawing.Color.FromArgb(222,222,222);
                }
                ApplyDarkTheme(child);
            }
        }

        private void Busy(bool value,string message)
        {
            busy=value;
            if(IsDisposed || Disposing) return;
            start.Enabled=abort.Enabled=cal.Enabled=!value;
            status.Text=message;
        }

        private async Task<string> Api(string method,string path,object payload=null)
        {
            return await Task.Run(()=>OperationsApi.Request(method,path,payload));
        }

        private static string Error(Exception ex)
        {
            var web=ex as WebException;
            if(web!=null)
            {
                var response=web.Response as HttpWebResponse;
                if(response!=null)
                {
                    int code=(int)response.StatusCode;
                    response.Dispose();
                    return "Cloud rejected request (HTTP "+code+").";
                }
            }
            return ex.Message;
        }

        private void LoadSurveyFile()
        {
            using(var dialog=new OpenFileDialog{
                Filter="Mission Planner waypoint draft (*.waypoints)|*.waypoints|Text (*.txt)|*.txt",
                Title="Open OFFLINE SURVEY draft (no upload)"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    var coordinates=new List<string>();
                    foreach(var line in File.ReadAllLines(dialog.FileName))
                    {
                        var f=line.Trim().Split('\t');
                        if(f.Length!=12 || f[0]=="0" || f[3]!="16" || f[2]!="3")continue;
                        coordinates.Add(f[8]+","+f[9]+","+f[10]);
                    }
                    if(coordinates.Count<2||coordinates.Count>80)
                        throw new ArgumentException("Draft must have 2-80 supported mission waypoints.");
                    missionText.Text=string.Join(Environment.NewLine,coordinates);
                    status.Text="Loaded "+coordinates.Count+" waypoint coordinates for simulation draft only.";
                }
                catch(Exception ex) when(ex is ArgumentException||ex is IOException||
                                           ex is UnauthorizedAccessException)
                {
                    status.Text="Cannot load draft: "+ex.Message;
                }
            }
        }

        private List<object> ParseWaypoints()
        {
            var list=new List<object>();
            foreach(var line in missionText.Lines)
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var fields=line.Split(',');
                if(fields.Length!=3)throw new ArgumentException("Each row needs latitude,longitude,altitude.");
                double lat,lon,alt;
                if(!double.TryParse(fields[0],NumberStyles.Float,CultureInfo.InvariantCulture,out lat)||
                   !double.TryParse(fields[1],NumberStyles.Float,CultureInfo.InvariantCulture,out lon)||
                   !double.TryParse(fields[2],NumberStyles.Float,CultureInfo.InvariantCulture,out alt))
                    throw new ArgumentException("Invalid numeric waypoint.");
                if(double.IsNaN(lat)||double.IsInfinity(lat)||lat< -90||lat>90||
                   double.IsNaN(lon)||double.IsInfinity(lon)||lon< -180||lon>180||
                   double.IsNaN(alt)||double.IsInfinity(alt)||alt<5||alt>120)
                    throw new ArgumentException("Waypoint outside validated limits.");
                list.Add(new{latitude=lat,longitude=lon,relative_altitude_m=alt});
            }
            if(list.Count<2||list.Count>80)
                throw new ArgumentException("Supply 2-80 waypoints.");
            return list;
        }

        private async Task CreateDraft()
        {
            if(busy)return;
            Busy(true,"Validating and submitting simulation draft...");
            try
            {
                var points=ParseWaypoints();
                var result=json.Deserialize<Dictionary<string,object>>(
                    await Api("POST","api/v1/operations/missions",
                        new{vehicle_id="AH-SIM-001",waypoints=points,speed_mps=3.0}));
                if(IsDisposed || Disposing) return;
                missionId.Text=Convert.ToString(result["mission_id"]);
                status.Text="SIMULATION DRAFT created: "+missionId.Text+
                    ". This is not uploaded to any real aircraft.";
            }catch(Exception ex){if(!IsDisposed && !Disposing) status.Text=Error(ex);}
            finally{if(!IsDisposed && !Disposing) Busy(false,status.Text); else busy=false;}
        }

        private async Task AcquireLease()
        {
            if(busy)return;
            Busy(true,"Acquiring simulation lease...");
            try
            {
                var reply=await Api("POST","api/v1/leases",new{vehicle_id="AH-SIM-001"});
                if(IsDisposed || Disposing) return;
                var doc=json.Deserialize<Dictionary<string,object>>(reply);
                leaseId.Text=Convert.ToString(doc["lease_id"]);
                leaseRenewal.Start();
                status.Text="Simulation-only lease acquired and scheduled for 10-second renewal. " +
                            "Closing OPS stops renewal; the server expires the lease automatically.";
            }catch(Exception ex){if(!IsDisposed && !Disposing) status.Text=Error(ex);}
            finally{if(!IsDisposed && !Disposing) Busy(false,status.Text); else busy=false;}
        }

        private async Task MissionAction(string action)
        {
            if(busy)return;
            if(string.IsNullOrWhiteSpace(missionId.Text))
            {
                status.Text="Create a simulation draft first.";return;
            }
            if(action=="start")
            {
                status.Text="Simulation execution requires a fresh operator lease. " +
                    "The current experimental API validates it and denies physical aircraft.";
            }
            Busy(true,"Submitting SIMULATION "+action+"...");
            try
            {
                // OperationsApi applies bearer authentication. A simulation lease
                // must be passed in X-Control-Lease and is never persisted.
                string selectedMission=missionId.Text;
                string selectedLease=leaseId.Text;
                string reply=await Task.Run(()=>RequestWithLease(action,selectedMission,
                    selectedLease));
                if(IsDisposed || Disposing) return;
                var doc=json.Deserialize<Dictionary<string,object>>(reply);
                status.Text="Simulation "+action+": "+Convert.ToString(doc["status"])+
                    ". This does NOT command Pixhawk hardware.";
            }
            catch(Exception ex){status.Text=Error(ex);}
            finally{if(!IsDisposed && !Disposing) Busy(false,status.Text); else busy=false;}
        }

        private string RequestWithLease(string action,string id,string lease)
        {
            if(string.IsNullOrWhiteSpace(lease))
                throw new InvalidOperationException("Request Simulator Control first.");
            if(string.IsNullOrWhiteSpace(Program.AeroHelmOperatorToken) ||
               DateTime.UtcNow>=Program.AeroHelmTokenExpiresUtc)
                throw new InvalidOperationException("Cloud session expired.");
            var origin=AeroHelmWorkspaceSettings.CheckedPrivateUrl(Program.AeroHelmCloudOrigin);
            string route=action=="renew" ? "api/v1/leases/AH-SIM-001/renew" :
                "api/v1/operations/missions/"+Uri.EscapeDataString(id)+"/"+action;
            var uri=new Uri(new Uri(origin.GetLeftPart(UriPartial.Authority)+"/"),route);
            var req=(HttpWebRequest)WebRequest.Create(uri);
            req.Method="POST";req.AllowAutoRedirect=false;req.Timeout=12000;
            req.Headers[HttpRequestHeader.Authorization]="Bearer "+Program.AeroHelmOperatorToken;
            req.Headers["X-Control-Lease"]=lease;
            byte[] bytes=Encoding.UTF8.GetBytes(action=="start"?
                json.Serialize(new{request_id=Guid.NewGuid().ToString()}):"{}");
            req.ContentType="application/json";req.ContentLength=bytes.Length;
            using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
            using(var response=(HttpWebResponse)req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        private async Task RenewSimulationLease()
        {
            if(renewing || string.IsNullOrWhiteSpace(leaseId.Text))return;
            renewing=true;
            try
            {
                string selectedMission=missionId.Text;
                string selectedLease=leaseId.Text;
                await Task.Run(()=>RequestWithLease("renew",selectedMission,selectedLease));
                if(IsDisposed || Disposing) return;
            }
            catch(Exception ex)
            {
                leaseRenewal.Stop();
                if(!IsDisposed && !Disposing)
                    status.Text="Simulation control lease renewal failed: "+Error(ex)+
                    ". The server will abort an active mission after lease expiry.";
            }
            finally { renewing=false; }
        }

        private async Task RefreshMission()
        {
            if(string.IsNullOrWhiteSpace(missionId.Text))return;
            string subject=missionId.Text;
            try
            {
                var body=await Api("GET","api/v1/operations/missions/"+
                    Uri.EscapeDataString(subject));
                if(IsDisposed || Disposing || missionId.Text!=subject) return;
                var info=json.Deserialize<Dictionary<string,object>>(body);
                status.Text="Mission "+subject+" | "+
                    Convert.ToString(info["status"])+" | waypoint "+
                    Convert.ToString(info["waypoint_index"])+" | metres "+
                    Convert.ToString(info["completed_distance_m"]);
            }catch(Exception ex){if(!IsDisposed && !Disposing) status.Text=Error(ex);}
        }

        private async Task RefreshCalibration()
        {
            if(string.IsNullOrEmpty(calibrationRequestId))
            {
                status.Text="Request a supervised calibration first.";return;
            }
            string requestId=calibrationRequestId;
            try
            {
                var body=await Api("GET","api/v1/operations/calibrations/"+
                    Uri.EscapeDataString(requestId));
                if(IsDisposed || Disposing || calibrationRequestId!=requestId) return;
                var info=json.Deserialize<Dictionary<string,object>>(body);
                status.Text="Calibration "+requestId+" | "+
                    Convert.ToString(info["status"])+" | "+Convert.ToString(info["detail"]);
            }catch(Exception ex){if(!IsDisposed && !Disposing) status.Text=Error(ex);}
        }

        private async Task Calibrate()
        {
            if(busy)return;
            if(!onsite.Checked||!props.Checked)
            {
                status.Text="On-site technician and propeller removal confirmations are mandatory.";
                return;
            }
            Busy(true,"Requesting single-use supervised calibration...");
            try
            {
                string id=Guid.NewGuid().ToString();
                string selectedKind=Convert.ToString(kind.SelectedItem);
                var result=json.Deserialize<Dictionary<string,object>>(
                    await Api("POST","api/v1/operations/calibrations",
                        new{request_id=id,kind=selectedKind,
                            human_at_aircraft=true,props_removed=true}));
                if(IsDisposed || Disposing) return;
                calibrationRequestId=id;
                status.Text="REQUESTED "+selectedKind+" | ID: "+id+Environment.NewLine+
                    "At the powered-on Pi terminal, operator must run:"+Environment.NewLine+
                    "cd ~/AeroHelm && agent/.venv/bin/python -m agent.supervised_calibration "+id+
                    Environment.NewLine+"Requires AEROHELM_SUPERVISED_CALIBRATION_ENABLED=true " +
                    "on the Pi agent. Expires after 45 seconds; actual result requires COMMAND_ACK.";
            }catch(Exception ex){if(!IsDisposed && !Disposing) status.Text=Error(ex);}
            finally{if(!IsDisposed && !Disposing) Busy(false,status.Text); else busy=false;}
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                leaseRenewal.Stop();
                leaseRenewal.Dispose();
                leaseId.Clear();
            }
            base.Dispose(disposing);
        }
    }
}

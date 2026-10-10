#region Copyright

// ASM OIB - Copyright (C) ASM AS GmbH & Co. KG
// All rights reserved.
// 
// The software and associated documentation supplied hereunder are
// the proprietary information of ASMand are supplied subject to license terms.

#endregion

#region using

using Asm.As.Oib.Common.Utilities;
using Asm.As.Oib.Monitoring.Proxy.Architecture.Objects;
using Asm.As.Oib.Monitoring.Proxy.Business.EventArgs;
using Asm.As.Oib.Monitoring.Proxy.Business.Objects;
using Asm.As.Oib.WS.Eventing.Contracts.Data;
using Asm.As.Oib.WS.Eventing.Contracts.Messages;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using Npgsql;
using System.Configuration;
using www.siplace.com.OIB._2008._05.ServiceLocator.Contracts.Data;
using www.siplace.com.OIB._2008._05.ServiceLocator.Contracts.Service;

#endregion

namespace OIB.Tutorial
{
    /// <summary>
    /// Demo App to Monitoring Tutorial
    /// </summary>
    /// <seealso cref="System.Windows.Forms.Form" />
    public partial class Form1 : Form
    { 
        #region Fields
        private Subscriber _subscriber;
        private ReliableReceiver _receiver;
        private Subscription _currentSubscription;
        private readonly System.Timers.Timer _timer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds);
        private System.Timers.Timer _autoStartTimer;
        private int _autoRetryCount = 0;
        private const int MaxAutoRetries = 10;
        private const int RetryIntervalSeconds = 60;
        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _trayMenu;
        #endregion

        #region Properties
        private DateTime NewExpiryDate
        {
            get { return DateTime.UtcNow + TimeSpan.FromDays(1); }
        }

        private string LastSubscriptionId
        {
            get { return UserSettings.Default.LastSubscriptionID; }
            set
            {
                _labelCurrentSubscriptionId.Text = UserSettings.Default.LastSubscriptionID = value;
                UserSettings.Default.Save();
            }
        }

        /// <summary>
        /// Gets the URI.
        /// </summary>
        /// <param name="uriKey">The URI key.</param>
        /// <param name="hostName">Name of the host.</param>
        /// <param name="doReplaceHostName">if set to <c>true</c> [do replace host name].</param>
        /// <returns></returns>
        private static string GetUri(string uriKey, string hostName, bool doReplaceHostName)
        {
            string uri = EndpointHelper.GetAppSettingString(uriKey);
            if (string.IsNullOrEmpty(uri))
            {
                throw new ArgumentNullException(uriKey);
            }

            if (doReplaceHostName)
            {
                // look for a host name
                var match = Regex.Match(uri, @"\/\/([\w]+):", RegexOptions.IgnoreCase);

                if (match.Groups.Count > 1)
                {
                    // a host name found, do replace it
                    uri = uri.Replace(match.Groups[1].Value, hostName);
                }
            }

            return uri;
        }

        private string CallbackEndpoint
        {
            get
            {
                var uri = GetUri("Asm.As.Oib.Monitoring.Proxy.ReceiverReliable", EndpointHelper.MachineName, !_checkBoxUseAppConfig.Checked);

                // change a port number
                if (!_checkBoxUseAppConfig.Checked)
                    uri = Regex.Replace(uri, @":[\d]+\/", ":" + _numericUpDownPort.Value + "/", RegexOptions.IgnoreCase);

                return uri;
            }
        }

        private string SubscriptionManagerEndpoint
        {
            get
            {
                return GetUri("Asm.As.Oib.Monitoring.Proxy.SubscriptionManagerEndpoint", _textBoxCoreName.Text, !_checkBoxUseAppConfig.Checked);
            }
        }

        private string ServiceLocatorEndpoint
        {
            get
            {
                return GetUri("Asm.As.Oib.Proxy.Servicelocator", _textBoxCoreName.Text, !_checkBoxUseAppConfig.Checked);
            }
        }

        private int LastPortNumber
        {
            get
            {
                if (!string.IsNullOrEmpty(UserSettings.Default.LastCallbackUri))
                {
                    Uri uri = new Uri(UserSettings.Default.LastCallbackUri);
                    return uri.Port;
                }
                return -1;
            }
        }
        #endregion

        #region Construction
        public Form1()
        {
            InitializeComponent();
            _textBoxCoreName.Text = EndpointHelper.MachineName;
            _timer.Elapsed += TimerOnElapsed;
            _buttonErrorCodes.Enabled = _buttonSubscribe.Enabled = _buttonUnsubscribe.Enabled = false;
            
            InitializeSystemTray();
            this.Resize += Form1_Resize;
            this.Load += Form1_Load;

            string executingDirectory = Assembly.GetExecutingAssembly().Location;
            foreach (string subDir in Directory.GetDirectories(Path.GetDirectoryName(executingDirectory)))
            {
                try
                {
                    string subDirName = new DirectoryInfo(subDir).Name;
                    var test = new CultureInfo(subDirName);
                    _comboBoxLanguage.Items.Add(subDirName);
                }
                catch 
                {
                    //
                }
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Read auto-start settings from App.config
            bool autoStart = false;
            int delaySeconds = 30;
            bool autoMinimized = true;

            try
            {
                string autoStartVal = ConfigurationManager.AppSettings["AutoStart"];
                if (!string.IsNullOrEmpty(autoStartVal))
                    bool.TryParse(autoStartVal, out autoStart);

                string delayVal = ConfigurationManager.AppSettings["AutoStartDelaySeconds"];
                if (!string.IsNullOrEmpty(delayVal))
                    int.TryParse(delayVal, out delaySeconds);

                string minimizedVal = ConfigurationManager.AppSettings["AutoStartMinimized"];
                if (!string.IsNullOrEmpty(minimizedVal))
                    bool.TryParse(minimizedVal, out autoMinimized);
            }
            catch (Exception ex)
            {
                AddMessage("Warning: Could not read AutoStart settings: " + ex.Message);
            }

            if (autoStart)
            {
                // Force checkboxes for auto mode
                _checkBoxUseAppConfig.Checked = true;
                _checkBoxUnsubscribe.Checked = true;

                AddMessage(string.Format("AutoStart enabled. Will connect in {0} seconds...", delaySeconds));

                // Minimize to tray if configured
                if (autoMinimized)
                {
                    this.WindowState = FormWindowState.Minimized;
                }

                // Start delay timer before auto-connect
                _autoStartTimer = new System.Timers.Timer(delaySeconds * 1000);
                _autoStartTimer.AutoReset = false;
                _autoStartTimer.Elapsed += (s, ev) =>
                {
                    if (InvokeRequired)
                        Invoke(new Action(AutoConnectAndSubscribe));
                    else
                        AutoConnectAndSubscribe();
                };
                _autoStartTimer.Start();
            }
        }

        private void AutoConnectAndSubscribe()
        {
            _autoRetryCount++;
            AddMessage(string.Format("AutoStart: Attempt {0}/{1} - Connecting to core...", _autoRetryCount, MaxAutoRetries));

            try
            {
                // Step 1: Initialize (Connect to core) - same as ButtonInitializeClick
                ButtonInitializeClick(null, EventArgs.Empty);

                // Check if initialize succeeded (button gets disabled on success)
                if (!_buttonInitialize.Enabled)
                {
                    AddMessage("AutoStart: Connected to core successfully.");

                    // Step 2: Subscribe - only if not already subscribed
                    if (_buttonSubscribe.Enabled)
                    {
                        ButtonSubscribeClick(null, EventArgs.Empty);
                        AddMessage("AutoStart: Subscribed to monitoring events.");
                    }
                    else
                    {
                        AddMessage("AutoStart: Already subscribed (reconnected to existing subscription).");
                    }

                    _autoRetryCount = 0; // Reset on success
                    return;
                }
            }
            catch (Exception ex)
            {
                AddMessage("AutoStart: Connection failed - " + ex.Message);
            }

            // Retry logic
            if (_autoRetryCount < MaxAutoRetries)
            {
                AddMessage(string.Format("AutoStart: Will retry in {0} seconds...", RetryIntervalSeconds));
                _autoStartTimer = new System.Timers.Timer(RetryIntervalSeconds * 1000);
                _autoStartTimer.AutoReset = false;
                _autoStartTimer.Elapsed += (s, ev) =>
                {
                    if (InvokeRequired)
                        Invoke(new Action(AutoConnectAndSubscribe));
                    else
                        AutoConnectAndSubscribe();
                };
                _autoStartTimer.Start();
            }
            else
            {
                AddMessage("AutoStart: Max retries reached. Please connect manually.");
            }
        }
        #endregion

        #region Helper Methods

        private List<string> GetMonitoringAdapters()
        {
            List<string> monitoringAdapters = new List<string>();
            try
            {
                // Add endpoint identity to provide information about service identity and open secure communication to service
                var address = new EndpointAddress(new Uri(ServiceLocatorEndpoint), EndpointIdentity.CreateDnsIdentity("ASM.SW.Service"));

                #region Client Authentication parameters

                // Parameters helper class that contains all the necessary information for binding and security settings

                // Option 1: This parameters indicates to use app.config central client authentication parameters
                var bindingParameters = new BindingParameters(address.ToString(), true);

                // Option 2: This parameters indicates to use other certificate for client authentication
                //var bindingParameters = new BindingParameters(address.ToString(), false)
                //{
                //    ClientCertificateParameters = new ServiceSecurityParameters
                //    {
                //        CertificateFindType = X509FindType.FindBySubjectName,
                //        CertificateStoreLocation = StoreLocation.CurrentUser,
                //        CertificateStoreName = StoreName.My,
                //        CertificateValue = "[Certificate Value used for OIB installation]"
                //    }
                //};

                #endregion

                ServiceLocatorClient slClient = new ServiceLocatorClient(EndpointHelper.CreateBindingFromParameters(bindingParameters), address);

                // Revocation list not used
                slClient.ChannelFactory.Credentials.ServiceCertificate.Authentication.RevocationMode = X509RevocationMode.NoCheck;

                #region Use Client Authentication

                // Usage of client authentication
                if (_cbUseClientAuthentication.Checked)
                {
                    // Add client certificate to authenticate to service

                    // **** IMPORTANT ****
                    // Option 1: Use app.config central client authentication parameters
                    slClient.ChannelFactory.Credentials.ClientCertificate.SetCertificate(
                        bindingParameters.ClientCertificateParameters.CertificateStoreLocation,
                        bindingParameters.ClientCertificateParameters.CertificateStoreName,
                        bindingParameters.ClientCertificateParameters.CertificateFindType,
                        bindingParameters.ClientCertificateParameters.CertificateValue);

                    // Option 2: use other client authentication parameters
                    //- the certificate value [Certificate Value used for OIB installation] must be set to that one selected during OIB installation
                    //slClient.ChannelFactory.Credentials.ClientCertificate.SetCertificate(
                    //  StoreLocation.CurrentUser, StoreName.My, X509FindType.FindBySubjectName, "[Certificate Value used for OIB installation]");
                }

                #endregion

                ServiceMatchCriteria criteria = new ServiceMatchCriteria{ServiceName = "SIPLACE.Monitoring"};
                ServiceDescription[] monitoringDescriptions = slClient.FindServices(criteria);
                foreach (ServiceDescription description in monitoringDescriptions)
                {
                    // This is a quick and dirty shortcut since we only use the computer name here and then 
                    // use a hard-coded endpoint pattern for the reliable tcp ASM endpoint
                    if (description.MetadataEndpoints.Length > 0)
                    {
                        Uri metadataEndpoint = description.MetadataEndpoints[0];
                        UriBuilder adapter = new UriBuilder(GetUri("Asm.As.Oib.Monitoring.Proxy.MonitoringEndpointReliable", _textBoxCoreName.Text, !_checkBoxUseAppConfig.Checked));
                        adapter.Host = metadataEndpoint.Host;
                        monitoringAdapters.Add(adapter.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                AddMessage("ERROR: While getting registered MonitoringAdapters from ServiceLocator: \n" + ex.Message);
            }
            return monitoringAdapters;
        }

        private void AddMessage(string message)
        {
            // === FILTER SYSTEM: specifically for Drop-part monitoring ===
            // Allow basic system/connection logs, explicit errors, and any Pickup/Drop/Feeder error events.
            bool isSystemLog = !message.Contains("ReceiverOn") && !message.Contains("State Transition:");
            bool isDropEvent = message.Contains("PickupError") || message.Contains("Drop") || message.Contains("FeederError");
            
            if (!isSystemLog && !isDropEvent)
            {
                return; // Ignore other noisy OIB events
            }

            if (InvokeRequired)
            {
                Invoke(new Action<string>(AddMessage), message);
            }
            else
            {
                _listViewMessages.Items.Add(new ListViewItem(new [] {DateTime.Now.ToShortTimeString(), message}));
                _listViewMessages.EnsureVisible(_listViewMessages.Items.Count-1);
            }
        }

        #region DOC_MONITORING_START_RECEIVER
        private void StartReceiver()
        {
            if (_receiver == null)
            {
                if (_checkBoxUseAppConfig.Checked)
                {
                    // Passing in false here will force to use the same port again (important in case the 
                    // client process (here: this app) has died and wants to reconnect to an existing subscription
                    // Then the endpoint of the receiver must be exactly the same (including port number)
                    _receiver = new ReliableReceiver(false);
                }
                else
                {
                    // Create the tcp binding depending on port sharing enabled setting in GUI.
                    // Tcp port sharing only works in case the process is elevated or the current windows user is allowed to use port sharing (see SDK)
                    var binding = EndpointHelper.CreateBindingFromEndpointString(CallbackEndpoint, true, _cbPortSharing.Checked);
                    _receiver = new ReliableReceiver(new EndpointAddress(CallbackEndpoint), binding, false);
                }

                UserSettings.Default.LastCallbackUri = _labelCallbackUri.Text = _receiver.CallbackEndpointString;
                UserSettings.Default.Save();

                // Subscribe to events
                _receiver.BoardProcessedEventReceived += ReceiverOnBoardProcessedEventReceived;
                _receiver.ConfigurationChangedEventReceived += ReceiverOnConfigurationChangedEventReceived;
                _receiver.PassmodeEventReceived += ReceiverOnPassmodeEventReceived;
                _receiver.RecipeActivationChangeEventReceived += ReceiverOnRecipeActivationChangeEventReceived;
                _receiver.RecipeChangeEventReceived += ReceiverOnRecipeChangeEventReceived;
                _receiver.RecipeDownloadEventReceived += ReceiverOnRecipeDownloadEventReceived;
                _receiver.SetupUpdateEventReceived += ReceiverOnSetupUpdateEventReceived;
                _receiver.StationEventReceived += ReceiverOnStationEventReceived;
                _receiver.PickupErrorEventReceived += ReceiverOnPickupErrorEventReceived;
                _receiver.HeartBeatEventReceived += ReceiverOnHeartBeatEventReceived;
                _receiver.StationAvailablilityEventReceived += ReceiverOnStationAvailablilityEventReceived;
                _receiver.FeederErrorEventReceived += ReceiverOnFeederErrorEventReceived;
                _checkBoxUseAppConfig.Enabled = false;
            }
        }

   

        #endregion

        #region DOC_MONITORING_STOP_RECEIVER
        private void StopReceiver()
        {
            if (_receiver != null)
            {
                // Unsubscribe from events
                _receiver.BoardProcessedEventReceived -= ReceiverOnBoardProcessedEventReceived;
                _receiver.ConfigurationChangedEventReceived -= ReceiverOnConfigurationChangedEventReceived;
                _receiver.PassmodeEventReceived -= ReceiverOnPassmodeEventReceived;
                _receiver.RecipeActivationChangeEventReceived -= ReceiverOnRecipeActivationChangeEventReceived;
                _receiver.RecipeChangeEventReceived -= ReceiverOnRecipeChangeEventReceived;
                _receiver.RecipeDownloadEventReceived -= ReceiverOnRecipeDownloadEventReceived;
                _receiver.SetupUpdateEventReceived -= ReceiverOnSetupUpdateEventReceived;
                _receiver.StationEventReceived -= ReceiverOnStationEventReceived;
                _receiver.PickupErrorEventReceived -= ReceiverOnPickupErrorEventReceived;
                _receiver.HeartBeatEventReceived -= ReceiverOnHeartBeatEventReceived;
                _receiver.StationAvailablilityEventReceived -= ReceiverOnStationAvailablilityEventReceived;
                _receiver.FeederErrorEventReceived -= ReceiverOnFeederErrorEventReceived;

                _receiver.Dispose();
                _receiver = null;
                UserSettings.Default.LastCallbackUri = _labelCallbackUri.Text = string.Empty;
                UserSettings.Default.Save();
                CheckBoxUseAppConfigCheckedChanged(null, null);
            }
        }
        #endregion

        #region DOC_MONITORING_CREATE_SUBSCRIPTION
        private void CreateSubscription()
        {
            if (_currentSubscription == null)
            {
                SubscribeResult result;
                // We need to create a new subscription
                if (_checkBoxFilterLine.Checked)
                {
                    XPathFilterAdapter filterAdapter = new XPathFilterAdapter(XPathFilterDataType.LineFullPath, _textBoxLineFullPath.Text);
                    result = _subscriber.Subscribe(filterAdapter, _receiver.CallbackEndpointString, NewExpiryDate, DeliveryModeType.PushWithAck);
                }
                else
                {
                    result = _subscriber.Subscribe(_receiver.CallbackEndpointString, NewExpiryDate, DeliveryModeType.PushWithAck);
                }

                LastSubscriptionId = result.SubscriptionManager.Identifier.ToString();
                SubscriptionDescriptor search = new SubscriptionDescriptor();
                search.Id = result.SubscriptionManager.Identifier.ToString();
                Subscription[] foundSubscriptions = _subscriber.GetSubscriptions(search);
                if (foundSubscriptions != null && foundSubscriptions.GetLength(0) > 0)
                {
                    _currentSubscription = foundSubscriptions[0];
                }
            }
        }
        #endregion

        private void RenewSubscription()
        {
            if (_currentSubscription != null)
            {
                // We should renew the subscription (make it expire 1 day up to now)
                DateTime expiresDate = NewExpiryDate;
                RenewRequest renew = new RenewRequest(_currentSubscription.Manager.Identifier, new Renew(new Expires(expiresDate)));
                _subscriber.Renew(renew);
                _currentSubscription.Expires = expiresDate;
                LastSubscriptionId = _currentSubscription.Manager.Identifier.ToString();
            }
        }

        private void DeleteSubscription()
        {
            if (_currentSubscription != null)
            {
                try
                {
                    _subscriber.Unsubscribe(_currentSubscription.Manager.Identifier, _subscriber.DefaultTopic);
                }
                catch (Exception ex)
                {
                    // Maybe the subscription was deleted?
                    AddMessage("Unsubscribe got exception: " +ex);
                }
                _currentSubscription = null;
                LastSubscriptionId = string.Empty;
            }
        }

        #endregion

        #region Event Handler
        private void TimerOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            try
            {
                _timer.Enabled = false;

                // Renew the subscription
                RenewSubscription();
            }
            catch (Exception ex)
            {
                AddMessage("Got exception when renewing the subscription: " + ex);
                
                // Auto-reconnect if AutoStart is enabled
                try
                {
                    string autoStartVal = ConfigurationManager.AppSettings["AutoStart"];
                    bool autoStart = false;
                    if (!string.IsNullOrEmpty(autoStartVal))
                        bool.TryParse(autoStartVal, out autoStart);
                    
                    if (autoStart && _autoRetryCount < MaxAutoRetries)
                    {
                        AddMessage("AutoStart: Subscription renewal failed. Attempting reconnect...");
                        _autoRetryCount = 0;
                        if (InvokeRequired)
                            Invoke(new Action(AutoConnectAndSubscribe));
                        else
                            AutoConnectAndSubscribe();
                    }
                }
                catch { /* Ignore reconnect errors in timer */ }
            }
            finally
            {
                _timer.Enabled = true;
            }
        }


#region DOC_MONITORING_LOGGING_STATE_TRANSTIONS

        private void ReceiverOnStationEventReceived(object sender, StationEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnStationEventReceived: "+ args.StationEventComposite);

            if (args.StationEventComposite != null)
            {
                if (args.StationEventComposite.StateTransitions != null)
                {
                    string stationName = (args.StationEventComposite.Station != null) ? args.StationEventComposite.Station.Name : "Unknown";
                    foreach (var transition in args.StationEventComposite.StateTransitions)
                    {
                        if (transition == null) continue;
                        AddMessage("  State Transition: Machine: " + stationName + 
                            " Processing area: " + transition.ProcessingArea + " into state " + transition.StationState);
                    }
                    
                }
            }
        }

#endregion 

        private void ReceiverOnSetupUpdateEventReceived(object sender, SetupCollectionEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnSetupUpdateEventReceived: " + args.SetupCollection);
        }

        private void ReceiverOnRecipeDownloadEventReceived(object sender, DownloadRecipeEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnRecipeDownloadEventReceived: " + args.DownloadRecipe);
        }

        private void ReceiverOnRecipeChangeEventReceived(object sender, RecipeChangeEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnRecipeChangeEventReceived: " + args.RecipeChange);
        }

        private void ReceiverOnRecipeActivationChangeEventReceived(object sender, RecipeActivationChangeEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnRecipeActivationChangeEventReceived: " + args.RecipeCollection);
        }

        private void ReceiverOnPassmodeEventReceived(object sender, PassmodeEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnPassmodeEventReceived: " + args.PassModeCollection);
        }

        private void ReceiverOnConfigurationChangedEventReceived(object sender, ConfigurationChangedEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnConfigurationChangedEventReceived: " + args.Configuration);
        }

        private void ReceiverOnBoardProcessedEventReceived(object sender, BoardProcessedEventArgs args)
        {
            if (args == null) return;
            AddMessage("ReceiverOnBoardProcessedEventReceived: " + args.BoardProcessedData);
            
            var data = args.BoardProcessedData;
            if (data == null || data.UsedDetails == null) return;
            
            // Extract machine/line info from board data
            string machineName = string.Empty;
            string lineName = string.Empty;
            string workOrder = string.Empty;
            string setupName = string.Empty;
            string boardName = string.Empty;
            string conveyorName = "Unknown";
            
            if (data.Recipe != null)
            {
                workOrder = data.Recipe.OrderId ?? string.Empty;
                setupName = data.Recipe.SetupFullPath ?? data.Recipe.SetupName ?? string.Empty;
                boardName = data.Recipe.BoardFullPath ?? data.Recipe.BoardName ?? string.Empty;
            }
            
            // Get station info directly from the board data's Station property
            if (data.Station != null)
            {
                machineName = data.Station.Name ?? string.Empty;
                lineName = data.Station.LineName ?? string.Empty;
            }

            if (data.ProcessedBoards != null && data.ProcessedBoards.Count > 0 && data.ProcessedBoards[0] != null)
            {
                conveyorName = data.ProcessedBoards[0].Conveyor.ToString();
            }

            // Insert one row per UsedDetail (one per feeder/part combination) inside a single transaction
            try
            {
                // Determine the best available event timestamp
                DateTime eventTime = DateTime.Now;
                if (data.StationEvents != null && data.StationEvents.Count > 0
                    && data.StationEvents[0] != null)
                {
                    // Use the station event timestamp if available
                    eventTime = data.StationEvents[0].StationTime.ToLocalTime();
                }

                using (var conn = new NpgsqlConnection(PgConnectionString))
                {
                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    {
                        foreach (var ud in data.UsedDetails)
                        {
                            if (ud == null) continue;
                        
                            string partNumber = string.Empty;
                            string componentShape = string.Empty;
                            string feederId = ud.FeederId ?? string.Empty;
                            int placedComponents = ud.PlacedComponents_Int;
                            int accessTotal = ud.AccessTotal_Int;        // Total pickup attempts (placed + drops)
                            int rejectIdent = ud.ExtendedRejects != null ? ud.ExtendedRejects.IdentError_Int : 0;        // Vision reject count
                            int rejectVacuum = ud.ExtendedRejects != null ? ud.ExtendedRejects.ComponentAbsenceAfterPick_Int : 0;      // Vacuum reject count
                            
                            string feederType = string.Empty;
                            string feederPn = string.Empty;
                            int trackNumber = 0;
                            int division = 0;
                            int tower = 0;
                            int level = 0;
                            string tableLocation = string.Empty;
                        
                            // Get part info from the TrackEntry
                            if (ud.TrackEntry != null)
                            {
                                partNumber = ud.TrackEntry.ComponentName ?? ud.TrackEntry.ComponentFullPath ?? string.Empty;
                                componentShape = ud.TrackEntry.ComponentShapeName ?? string.Empty;
                                feederType = ud.TrackEntry.FeederTypeName ?? string.Empty;
                                feederPn = (ud.TrackEntry.FeederType != null) ? ud.TrackEntry.FeederType.ToString() : string.Empty;
                                trackNumber = ud.TrackEntry.Track;
                                division = ud.TrackEntry.Division;
                                tower = ud.TrackEntry.Tower;
                                level = ud.TrackEntry.Level;
                                tableLocation = (ud.TrackEntry.TableLocation != null) ? ud.TrackEntry.TableLocation.ToString() : string.Empty;
                            }
                        
                            // Skip rows with zero placements (inactive feeders)
                            if (placedComponents == 0 && accessTotal == 0) continue;
                        
                            string sql = @"
                                INSERT INTO oib_placements (
                                    station_time, line_name, machine_name,
                                    part_number, component_shape, feeder_id, feeder_type, feeder_pn,
                                    track_number, division, tower, level, table_location,
                                    placed_quantity, access_total, reject_ident, reject_vacuum,
                                    work_order, setup_name, board_name, conveyor
                                ) VALUES (
                                    @stationTime, @line, @machine,
                                    @partNo, @shape, @feederId, @feederType, @feederPn,
                                    @track, @division, @tower, @level, @location,
                                    @placed, @accessTotal, @rejectIdent, @rejectVacuum,
                                    @workOrder, @setupName, @boardName, @conveyor
                                )";
                        
                            using (var cmd = new NpgsqlCommand(sql, conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("stationTime", eventTime);
                                cmd.Parameters.AddWithValue("line", string.IsNullOrEmpty(lineName) ? (object)DBNull.Value : Truncate(lineName));
                                cmd.Parameters.AddWithValue("machine", string.IsNullOrEmpty(machineName) ? (object)DBNull.Value : Truncate(machineName));
                                cmd.Parameters.AddWithValue("partNo", string.IsNullOrEmpty(partNumber) ? (object)DBNull.Value : Truncate(partNumber));
                                cmd.Parameters.AddWithValue("shape", string.IsNullOrEmpty(componentShape) ? (object)DBNull.Value : Truncate(componentShape));
                                cmd.Parameters.AddWithValue("feederId", string.IsNullOrEmpty(feederId) ? (object)DBNull.Value : Truncate(feederId));
                                cmd.Parameters.AddWithValue("feederType", string.IsNullOrEmpty(feederType) ? (object)DBNull.Value : Truncate(feederType));
                                cmd.Parameters.AddWithValue("feederPn", string.IsNullOrEmpty(feederPn) ? (object)DBNull.Value : Truncate(feederPn));
                                cmd.Parameters.AddWithValue("track", trackNumber);
                                cmd.Parameters.AddWithValue("division", division);
                                cmd.Parameters.AddWithValue("tower", tower);
                                cmd.Parameters.AddWithValue("level", level);
                                cmd.Parameters.AddWithValue("location", string.IsNullOrEmpty(tableLocation) ? (object)DBNull.Value : Truncate(tableLocation));
                                cmd.Parameters.AddWithValue("placed", placedComponents);
                                cmd.Parameters.AddWithValue("accessTotal", accessTotal);
                                cmd.Parameters.AddWithValue("rejectIdent", rejectIdent);
                                cmd.Parameters.AddWithValue("rejectVacuum", rejectVacuum);
                                cmd.Parameters.AddWithValue("workOrder", string.IsNullOrEmpty(workOrder) ? (object)DBNull.Value : Truncate(workOrder));
                                cmd.Parameters.AddWithValue("setupName", string.IsNullOrEmpty(setupName) ? (object)DBNull.Value : Truncate(setupName));
                                cmd.Parameters.AddWithValue("boardName", string.IsNullOrEmpty(boardName) ? (object)DBNull.Value : Truncate(boardName));
                                cmd.Parameters.AddWithValue("conveyor", string.IsNullOrEmpty(conveyorName) ? (object)DBNull.Value : Truncate(conveyorName));
                            
                                cmd.ExecuteNonQuery();
                            }
                        }

                        // --- Insert one row per ProcessedBoard into oib_board_log ---
                        if (data.ProcessedBoards != null)
                        {
                            string boardSql = @"
                                INSERT INTO oib_board_log (
                                    station_time, line_name, machine_name,
                                    barcode, board_name, board_number, panel_count,
                                    conveyor, build_time_seconds, board_status,
                                    work_order, setup_name
                                ) VALUES (
                                    @stationTime, @line, @machine,
                                    @barcode, @boardName, @boardNumber, @panelCount,
                                    @conveyor, @buildTime, @boardStatus,
                                    @workOrder, @setupName
                                )";

                            foreach (var pb in data.ProcessedBoards)
                            {
                                if (pb == null) continue;
                                using (var bc = new NpgsqlCommand(boardSql, conn, transaction))
                                {
                                    bc.Parameters.AddWithValue("stationTime", pb.StationTime.ToLocalTime());
                                    bc.Parameters.AddWithValue("line", string.IsNullOrEmpty(lineName) ? (object)DBNull.Value : Truncate(lineName));
                                    bc.Parameters.AddWithValue("machine", string.IsNullOrEmpty(machineName) ? (object)DBNull.Value : Truncate(machineName));
                                    bc.Parameters.AddWithValue("barcode", string.IsNullOrEmpty(pb.Barcode) ? (object)DBNull.Value : Truncate(pb.Barcode));
                                    bc.Parameters.AddWithValue("boardName", string.IsNullOrEmpty(pb.BoardName) ? (object)DBNull.Value : Truncate(pb.BoardName));
                                    bc.Parameters.AddWithValue("boardNumber", pb.BoardNumber);
                                    bc.Parameters.AddWithValue("panelCount", pb.PanelCount);
                                    bc.Parameters.AddWithValue("conveyor", pb.Conveyor.ToString());
                                    bc.Parameters.AddWithValue("buildTime", pb.BuildTimeInt);
                                    bc.Parameters.AddWithValue("boardStatus", pb.Status.ToString());
                                    bc.Parameters.AddWithValue("workOrder", string.IsNullOrEmpty(workOrder) ? (object)DBNull.Value : Truncate(workOrder));
                                    bc.Parameters.AddWithValue("setupName", string.IsNullOrEmpty(setupName) ? (object)DBNull.Value : Truncate(setupName));
                                    bc.ExecuteNonQuery();
                                }
                            }
                        }

                        transaction.Commit();
                    }
                }
                
                int totalPlaced = 0;
                foreach (var ud in data.UsedDetails)
                    if (ud != null) totalPlaced += ud.PlacedComponents_Int;
                    
                AddMessage(string.Format("BoardProcessed: Logged {0} placements for {1} feeder entries.", totalPlaced, data.UsedDetails.Count));
            }
            catch (Exception ex)
            {
                AddMessage("ERROR inserting board placements to DB: " + ex.Message);
            }
        }

        private void ReceiverOnPickupErrorEventReceived(object sender, PickupErrorEventArgs args)
        {
            if (args == null) return;
            string partNumber = "Unknown";
            string componentShape = string.Empty;
            string machineName = string.Empty;
            string lineName = string.Empty;
            DateTime stationTime = DateTime.Now;
            string feederId = string.Empty;
            string feederType = string.Empty;
            string feederPn = string.Empty;
            int trackNumber = 0;
            int division = 0;
            int tower = 0;
            int level = 0;
            string tableLocation = string.Empty;
            string workOrder = string.Empty;
            string setupName = string.Empty;
            string boardFullPath = string.Empty;
            int feederSlot = 0;
            int gantryNumber = 0;
            int headNumber = 0;
            int nozzleNumber = 0;
            int segmentNumber = 0;
            string errorDescription = string.Empty;
            int errorType = 0;
            int classification = 0;
            long messageOid = 0;
            string conveyorName = "Unknown";

            if (args.PickupErrorEventData != null && args.PickupErrorEventData.PickupError != null)
            {
                var pickupError = args.PickupErrorEventData.PickupError;
                stationTime = pickupError.StationTime.ToLocalTime();
                feederSlot = pickupError.TrackEntry != null ? pickupError.TrackEntry.Track : 0;
                gantryNumber = pickupError.Gantry;
                headNumber = pickupError.Head;
                nozzleNumber = pickupError.Nozzle;
                segmentNumber = pickupError.Segment;
                errorDescription = pickupError.ErrorDescription;
                errorType = pickupError.ErrorType;
                classification = pickupError.Classification;
                messageOid = pickupError.MessageOID;

                feederId = pickupError.FeederId;

                if (args.PickupErrorEventData.Station != null)
                {
                    machineName = args.PickupErrorEventData.Station.Name;
                    lineName = args.PickupErrorEventData.Station.LineName;
                }

                if (pickupError.TrackEntry != null)
                {
                    partNumber = pickupError.TrackEntry.ComponentName;
                    if (string.IsNullOrEmpty(partNumber))
                    {
                        partNumber = pickupError.TrackEntry.ComponentFullPath;
                    }
                    componentShape = pickupError.TrackEntry.ComponentShapeName;
                    feederType = pickupError.TrackEntry.FeederTypeName;
                    feederPn = (pickupError.TrackEntry.FeederType != null) ? pickupError.TrackEntry.FeederType.ToString() : string.Empty;
                    trackNumber = pickupError.TrackEntry.Track;
                    division = pickupError.TrackEntry.Division;
                    tower = pickupError.TrackEntry.Tower;
                    level = pickupError.TrackEntry.Level;
                    tableLocation = (pickupError.TrackEntry.TableLocation != null) ? pickupError.TrackEntry.TableLocation.ToString() : string.Empty;
                }

                if (pickupError.AffectedBoards != null && pickupError.AffectedBoards.Count > 0 && pickupError.AffectedBoards[0] != null)
                {
                    conveyorName = "Conveyor " + pickupError.AffectedBoards[0].Conveyor.ToString();
                }

                if (args.PickupErrorEventData.Station != null && !string.IsNullOrEmpty(args.PickupErrorEventData.Station.FullPath))
                {
                    Monitoring monitoringInstance = args.PickupErrorEventData.MonitoringInstance;

                    if (monitoringInstance == null)
                    {
                        // Fallback lookup if MonitoringInstance is null (R23 behavior in some cases)
                        try
                        {
                            string stationPath = args.PickupErrorEventData.Station.FullPath;
                            foreach (string adapterEP in GetMonitoringAdapters())
                            {
                                EndpointAddress epa = new EndpointAddress(adapterEP);
                                var tempInstance = MonitoringManager.GetOrCreateInstance(epa, EndpointHelper.CreateBindingFromEndpointString(adapterEP, true, true));
                                var config = tempInstance.GetConfiguration();
                                if (config != null && config.Lines != null)
                                {
                                    foreach (var l in config.Lines)
                                    {
                                        if (l.Stations != null)
                                        {
                                            foreach (var s in l.Stations)
                                            {
                                                if (s.FullPath == stationPath)
                                                {
                                                    monitoringInstance = tempInstance;
                                                    break;
                                                }
                                            }
                                        }
                                        if (monitoringInstance != null) break;
                                    }
                                }
                                if (monitoringInstance != null) break;
                            }
                        }
                        catch { /* Ignore fallback lookup errors */ }
                    }

                    if (monitoringInstance != null)
                    {
                        try
                        {
                            var recipes = monitoringInstance.GetActiveRecipesForStation(args.PickupErrorEventData.Station.FullPath);
                            if (recipes != null && recipes.RecipeLocations != null && recipes.RecipeLocations.Count > 0)
                            {
                                var recipe = recipes.RecipeLocations[0].Recipe;
                                if (recipe != null)
                                {
                                    workOrder = recipe.OrderId;
                                    setupName = recipe.SetupFullPath ?? recipe.SetupName;
                                    boardFullPath = recipe.BoardFullPath ?? recipe.BoardName;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AddMessage(string.Format("Warning: Could not fetch Work Order/Setup: {0}", ex.Message));
                        }
                    }
                }
            }

            // Log to Text File
            AddMessage(string.Format("ReceiverOnPickupErrorEventReceived: PartNumber: {0}, {1}", partNumber, args.PickupErrorEventData));

            // Insert to PostgreSQL Server (with retry)
            int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using (var conn = new NpgsqlConnection(PgConnectionString))
                    {
                        conn.Open();
                        string sql = @"
                            INSERT INTO oib_pickup_errors (
                                station_time, line_name, machine_name,
                                setup_name, work_order,
                                part_number, component_shape, feeder_id, feeder_type, feeder_slot,
                                track_number, division, tower, level, table_location,
                                gantry_number, head_number, nozzle_number, segment_number,
                                error_description, error_type_id, classification, message_oid, conveyor
                            ) VALUES (
                                @stationTime, @line, @machine,
                                @setupName, @workOrder,
                                @partNo, @shape, @feederId, @feederType, @feederSlot,
                                @track, @division, @tower, @level, @location,
                                @gantry, @head, @nozzle, @segment,
                                @descr, @errType, @class, @oid, @conveyor
                            )";

                        using (var cmd = new NpgsqlCommand(sql, conn))
                        {
                            cmd.Parameters.AddWithValue("stationTime", stationTime);
                            cmd.Parameters.AddWithValue("line", string.IsNullOrEmpty(lineName) ? (object)DBNull.Value : Truncate(lineName));
                            cmd.Parameters.AddWithValue("machine", string.IsNullOrEmpty(machineName) ? (object)DBNull.Value : Truncate(machineName));
                            cmd.Parameters.AddWithValue("partNo", string.IsNullOrEmpty(partNumber) ? (object)DBNull.Value : Truncate(partNumber));
                            cmd.Parameters.AddWithValue("shape", string.IsNullOrEmpty(componentShape) ? (object)DBNull.Value : Truncate(componentShape));
                            cmd.Parameters.AddWithValue("feederId", string.IsNullOrEmpty(feederId) ? (object)DBNull.Value : Truncate(feederId));
                            cmd.Parameters.AddWithValue("feederType", string.IsNullOrEmpty(feederType) ? (object)DBNull.Value : Truncate(feederType));
                            cmd.Parameters.AddWithValue("feederSlot", feederSlot);
                            cmd.Parameters.AddWithValue("track", trackNumber);
                            cmd.Parameters.AddWithValue("division", division);
                            cmd.Parameters.AddWithValue("tower", tower);
                            cmd.Parameters.AddWithValue("level", level);
                            cmd.Parameters.AddWithValue("location", string.IsNullOrEmpty(tableLocation) ? (object)DBNull.Value : Truncate(tableLocation));
                            cmd.Parameters.AddWithValue("gantry", gantryNumber);
                            cmd.Parameters.AddWithValue("head", headNumber);
                            cmd.Parameters.AddWithValue("nozzle", nozzleNumber);
                            cmd.Parameters.AddWithValue("segment", segmentNumber);
                            cmd.Parameters.AddWithValue("descr", string.IsNullOrEmpty(errorDescription) ? (object)DBNull.Value : Truncate(errorDescription));
                            cmd.Parameters.AddWithValue("errType", errorType);
                            cmd.Parameters.AddWithValue("class", classification);
                            cmd.Parameters.AddWithValue("oid", messageOid);
                            cmd.Parameters.AddWithValue("workOrder", string.IsNullOrEmpty(workOrder) ? (object)DBNull.Value : Truncate(workOrder));
                            cmd.Parameters.AddWithValue("setupName", string.IsNullOrEmpty(setupName) ? (object)DBNull.Value : Truncate(setupName));
                            cmd.Parameters.AddWithValue("conveyor", string.IsNullOrEmpty(conveyorName) ? (object)DBNull.Value : Truncate(conveyorName));

                            cmd.ExecuteNonQuery();
                        }
                    }
                    break; // Success
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        AddMessage("ERROR final attempt inserting pickup error to DB: " + ex.Message);
                    }
                    else
                    {
                        Thread.Sleep(500); // Wait bit before retry
                    }
                }
            }
        }


        private void ReceiverOnHeartBeatEventReceived(object sender, HeartBeatEventArgs heartBeatEventArgs)
        {
            if (heartBeatEventArgs == null) return;
            AddMessage("ReceiverOnHeartBeatEventReceived: " + heartBeatEventArgs.HeartBeatEventData);
        }

        private void ReceiverOnFeederErrorEventReceived(object sender, FeederErrorEventArgs args)
        {
            if (args == null || args.FeederErrorEventData == null) return;
            var data = args.FeederErrorEventData;
            AddMessage("ReceiverOnFeederErrorEventReceived: " + data);

            string lineName    = data.Station != null ? data.Station.LineName ?? string.Empty : string.Empty;
            string machineName = data.Station != null ? data.Station.Name    ?? string.Empty : string.Empty;
            DateTime eventTime = DateTime.Now;

            string feederId    = string.Empty;
            string feederType  = string.Empty;
            int    feederCode  = 0;
            int    tableLocation = 0;
            int    track = 0, tower = 0, level = 0, division = 0;

            if (data.FeederLocation != null)
            {
                feederId     = data.FeederLocation.FeederID     ?? string.Empty;
                feederType   = data.FeederLocation.FeederTypeName ?? string.Empty;
                feederCode   = data.FeederLocation.FeederCode;
                tableLocation = data.FeederLocation.TableLocation;
                track        = data.FeederLocation.Track;
                tower        = data.FeederLocation.Tower;
                level        = data.FeederLocation.Level;
                division     = data.FeederLocation.Division;
            }

            int    errorCode    = 0;
            string errorDesc    = string.Empty;
            if (data.ErrorData != null)
            {
                errorCode  = data.ErrorData.Code;
                errorDesc  = data.ErrorData.ErrorDescription ?? string.Empty;
                eventTime  = data.ErrorData.Timestamp.ToLocalTime();
            }

            try
            {
                using (var conn = new NpgsqlConnection(PgConnectionString))
                {
                    conn.Open();
                    string sql = @"
                        INSERT INTO oib_feeder_errors (
                            station_time, line_name, machine_name,
                            feeder_id, feeder_type, feeder_code,
                            error_code, error_description,
                            table_location, track, tower, level, division
                        ) VALUES (
                            @stationTime, @line, @machine,
                            @feederId, @feederType, @feederCode,
                            @errorCode, @errorDesc,
                            @tableLocation, @track, @tower, @level, @division
                        )";
                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("stationTime",   eventTime);
                        cmd.Parameters.AddWithValue("line",          string.IsNullOrEmpty(lineName)    ? (object)DBNull.Value : Truncate(lineName));
                        cmd.Parameters.AddWithValue("machine",       string.IsNullOrEmpty(machineName) ? (object)DBNull.Value : Truncate(machineName));
                        cmd.Parameters.AddWithValue("feederId",      string.IsNullOrEmpty(feederId)    ? (object)DBNull.Value : Truncate(feederId));
                        cmd.Parameters.AddWithValue("feederType",    string.IsNullOrEmpty(feederType)  ? (object)DBNull.Value : Truncate(feederType));
                        cmd.Parameters.AddWithValue("feederCode",    feederCode);
                        cmd.Parameters.AddWithValue("errorCode",     errorCode);
                        cmd.Parameters.AddWithValue("errorDesc",     string.IsNullOrEmpty(errorDesc)   ? (object)DBNull.Value : errorDesc);
                        cmd.Parameters.AddWithValue("tableLocation", tableLocation);
                        cmd.Parameters.AddWithValue("track",         track);
                        cmd.Parameters.AddWithValue("tower",         tower);
                        cmd.Parameters.AddWithValue("level",         level);
                        cmd.Parameters.AddWithValue("division",      division);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                AddMessage("ERROR inserting feeder error to DB: " + ex.Message);
            }
        }

#region DOC_MONITORING_LOGGING_NEW_STATE_TRANSTIONS

        private void ReceiverOnStationAvailablilityEventReceived(object sender, StationAvailabilityEventArgs args)
        {
            if (args == null || args.StationAvailabilityCollection == null) return;
            AddMessage("ReceiverOnStationAvailablilityEventReceived. " + args.StationAvailabilityCollection);

            var collection = args.StationAvailabilityCollection;
            string stationName = (collection.Station != null) ? collection.Station.Name    ?? "Unknown" : "Unknown";
            string lineName    = (collection.Station != null) ? collection.Station.LineName ?? string.Empty : string.Empty;

            if (collection.ProcessingLocationAvailabilityStates == null) return;

            foreach (var state in collection.ProcessingLocationAvailabilityStates)
            {
                if (state == null) continue;

                string locationId    = state.ProcessingLocationId.ToString();
                string prevState     = state.PreviousStateCodeMes ?? string.Empty;
                string currState     = state.CurrentStateCodeMes  ?? string.Empty;
                string primaryErrNo  = state.PrimaryErrorNo       ?? string.Empty;
                DateTime stateTime   = state.CurrentStateBegin.ToLocalTime();

                AddMessage(string.Format("State Transition: {0} [{1}] {2} → {3} at {4}",
                    stationName, locationId, prevState, currState, stateTime));

                // Collect first reason's ErrorNo + AssistId for the transition row
                string firstErrorNo = string.Empty;
                long   firstAssistId = 0;
                if (state.StateChangeTriggers != null)
                {
                    foreach (StateChangeTrigger trigger in state.StateChangeTriggers)
                    {
                        if (trigger?.StateChangeReasons == null) continue;
                        foreach (StateChangeReason reason in trigger.StateChangeReasons)
                        {
                            if (reason == null) continue;
                            if (string.IsNullOrEmpty(firstErrorNo) && !string.IsNullOrEmpty(reason.ErrorNo))
                                firstErrorNo = reason.ErrorNo;
                            if (firstAssistId == 0 && reason.AssistId != 0)
                                firstAssistId = reason.AssistId;
                            AddMessage(string.Format("  Reason — AssistId: {0} ErrorNo: {1}", reason.AssistId, reason.ErrorNo));
                        }
                    }
                }

                try
                {
                    using (var conn = new NpgsqlConnection(PgConnectionString))
                    {
                        conn.Open();

                        // Insert into oib_state_transitions
                        string transSql = @"
                            INSERT INTO oib_state_transitions (
                                station_time, line_name, machine_name,
                                processing_location, previous_state, current_state,
                                primary_error_no, assist_id, error_no
                            ) VALUES (
                                @stationTime, @line, @machine,
                                @location, @prevState, @currState,
                                @primaryErrNo, @assistId, @errorNo
                            )";

                        using (var cmd = new NpgsqlCommand(transSql, conn))
                        {
                            cmd.Parameters.AddWithValue("stationTime",  stateTime);
                            cmd.Parameters.AddWithValue("line",         string.IsNullOrEmpty(lineName)   ? (object)DBNull.Value : Truncate(lineName));
                            cmd.Parameters.AddWithValue("machine",      string.IsNullOrEmpty(stationName)? (object)DBNull.Value : Truncate(stationName));
                            cmd.Parameters.AddWithValue("location",     Truncate(locationId));
                            cmd.Parameters.AddWithValue("prevState",    string.IsNullOrEmpty(prevState)  ? (object)DBNull.Value : Truncate(prevState));
                            cmd.Parameters.AddWithValue("currState",    string.IsNullOrEmpty(currState)  ? (object)DBNull.Value : Truncate(currState));
                            cmd.Parameters.AddWithValue("primaryErrNo", string.IsNullOrEmpty(primaryErrNo)? (object)DBNull.Value : Truncate(primaryErrNo));
                            cmd.Parameters.AddWithValue("assistId",     firstAssistId == 0 ? (object)DBNull.Value : firstAssistId);
                            cmd.Parameters.AddWithValue("errorNo",      string.IsNullOrEmpty(firstErrorNo)? (object)DBNull.Value : Truncate(firstErrorNo));
                            cmd.ExecuteNonQuery();
                        }

                        // If there is an error code → also log to oib_machine_errors
                        bool hasError = !string.IsNullOrEmpty(primaryErrNo) || !string.IsNullOrEmpty(firstErrorNo) || firstAssistId != 0;
                        if (hasError)
                        {
                            string errorSql = @"
                                INSERT INTO oib_machine_errors (
                                    station_time, line_name, machine_name,
                                    processing_location, error_no, assist_id, description
                                ) VALUES (
                                    @stationTime, @line, @machine,
                                    @location, @errorNo, @assistId, @description
                                )";

                            using (var cmd = new NpgsqlCommand(errorSql, conn))
                            {
                                string errNo = !string.IsNullOrEmpty(firstErrorNo) ? firstErrorNo : primaryErrNo;
                                string desc  = string.Format("{0} → {1}", prevState, currState);
                                cmd.Parameters.AddWithValue("stationTime", stateTime);
                                cmd.Parameters.AddWithValue("line",        string.IsNullOrEmpty(lineName)   ? (object)DBNull.Value : Truncate(lineName));
                                cmd.Parameters.AddWithValue("machine",     string.IsNullOrEmpty(stationName)? (object)DBNull.Value : Truncate(stationName));
                                cmd.Parameters.AddWithValue("location",    Truncate(locationId));
                                cmd.Parameters.AddWithValue("errorNo",     string.IsNullOrEmpty(errNo)      ? (object)DBNull.Value : Truncate(errNo));
                                cmd.Parameters.AddWithValue("assistId",    firstAssistId == 0 ? (object)DBNull.Value : firstAssistId);
                                cmd.Parameters.AddWithValue("description", Truncate(desc));
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddMessage("ERROR inserting state transition to DB: " + ex.Message);
                }
            }
        }

#endregion

        private void ButtonSubscribeClick(object sender, EventArgs e)
        {
            StartReceiver();
            CreateSubscription();
            _numericUpDownPort.Enabled = _buttonSubscribe.Enabled = false;
            _buttonUnsubscribe.Enabled = true;
            _timer.Enabled = true;
        }

        private void ButtonUnsubscribeClick(object sender, EventArgs e)
        {
            DeleteSubscription();
            _numericUpDownPort.Enabled = _buttonSubscribe.Enabled = true;
            _buttonUnsubscribe.Enabled = false;
            _timer.Enabled = false;
            StopReceiver();
        }

        private void ButtonInitializeClick(object sender, EventArgs e)
        {
            try
            {
                #region DOC_MONITORING_CREATE_SUBSCRIBER

                #region Use Client Authentication

                // Usage of client authentication
                if (_cbUseClientAuthentication.Checked)
                {
                    // Add client certificate to authenticate to service

                    // **** IMPORTANT ****
                    //- the certificate value [Certificate Value used for OIB installation] must be set to that one selected during OIB installation
                    //- uncomment to use client authentication, but consider these values overrides app.config settings
                    //- used with Option 2 (see below)
                    //Subscriber.SetCertificateForClientAuthentication(
                    //    StoreLocation.CurrentUser, X509FindType.FindBySubjectName, StoreName.My, "[Certificate Value used for OIB installation]");
                }

                #endregion

                if (_checkBoxUseAppConfig.Checked)
                {
                    _subscriber = new Subscriber();
                }
                else
                {
                    string spSM = SubscriptionManagerEndpoint;

                    #region Client Authentication parameters

                    // Parameters helper class that contains all the necessary information for binding and security settings

                    // Option 1: This parameters indicates to use app.config central client authentication parameters
                    var bindingParameters = new BindingParameters(spSM, true);

                    // Option 2: This parameters indicates to use other certificate for client authentication
                    //var bindingParameters = new BindingParameters(address.ToString(), false)
                    //{
                    //    ClientCertificateParameters = new ServiceSecurityParameters
                    //    {
                    //        CertificateFindType = X509FindType.FindBySubjectName,
                    //        CertificateStoreLocation = StoreLocation.CurrentUser,
                    //        CertificateStoreName = StoreName.My,
                    //        CertificateValue = "[Certificate Value used for OIB installation]"
                    //    }
                    //};

                    #endregion

                    _subscriber = new Subscriber(
                        new EndpointAddress(new Uri(spSM), EndpointIdentity.CreateDnsIdentity("ASM.SW.Service")), EndpointHelper.CreateBindingFromParameters(bindingParameters));
                }

                #endregion

                #region DOC_MONITORING_LAST_SUBSCRIPTION_ID

                string lastSubsciptionId = LastSubscriptionId;
                if (!string.IsNullOrEmpty(lastSubsciptionId))
                {
                    // See if there is still a subscription with this ID.
                    // If yes, then renew it...
                    SubscriptionDescriptor search = new SubscriptionDescriptor();
                    search.Id = lastSubsciptionId;
                    Subscription[] foundSubscriptions = _subscriber.GetSubscriptions(search);
                    if (foundSubscriptions != null && foundSubscriptions.GetLength(0) > 0)
                    {
                        // Check that the subscription that we found is using the same callback
                        Uri foundCallbackUri = foundSubscriptions[0].Delivery.EndpointSerialiazable.ToEndpointAddress().Uri;
                        if (foundCallbackUri.ToString() != CallbackEndpoint)
                        {
                            MessageBox.Show(string.Format("A subscription with Identifier:{0} was found in Eventing which uses a different callback than specified! Please manually delete the old subscription!.", lastSubsciptionId));
                        }
                        else
                        {
                            _currentSubscription = foundSubscriptions[0];
                        }
                    }
                }
                #endregion

                #region DOC_MONITORING_LAST_SUBSCRIPTION_CALLBACK
                else
                {
                    // Alternative 2, use the callback URI and the topic to get the existing subscription
                    SubscriptionDescriptor search = new SubscriptionDescriptor();
                    search.Topic = _subscriber.DefaultTopic;
                    search.Endpoint = EndpointAddressAugust2004.FromEndpointAddress(new EndpointAddress(CallbackEndpoint));
                    Subscription[] foundSubscriptions = _subscriber.GetSubscriptions(search);
                    if (foundSubscriptions != null && foundSubscriptions.GetLength(0) > 0)
                    {
                        _currentSubscription = foundSubscriptions[0];
                    }
                }
                #endregion

                int lastPortNumber = LastPortNumber;
                if (lastPortNumber != -1)
                {
                    _numericUpDownPort.Value = lastPortNumber;
                }

                #region DOC_MONITORING_SUBSCRIPTION_FOUND
                if (_currentSubscription != null)
                {
                    StartReceiver();
                    RenewSubscription();
                    _labelCurrentSubscriptionId.Text = _currentSubscription.Manager.Identifier.ToString();
                    _numericUpDownPort.Enabled = _buttonSubscribe.Enabled = false;
                    _buttonUnsubscribe.Enabled = true;
                    _timer.Enabled = true;
                }
                else
                {
                    _numericUpDownPort.Enabled = _buttonSubscribe.Enabled = true;
                    _buttonUnsubscribe.Enabled = false;
                }
                #endregion

                _textBoxCoreName.Enabled = _buttonInitialize.Enabled = false;

                _listBoxAdapters.Items.Clear();
                foreach (string monitoringAdapterEP in GetMonitoringAdapters())
                {
                    _listBoxAdapters.Items.Add(monitoringAdapterEP);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error happened when connecting to core: " + ex);
            }
        }

        private void Form1Closing(object sender, FormClosingEventArgs e)
        {
            if (_checkBoxUnsubscribe.Checked)
            {
                DeleteSubscription();
            }
            _timer.Enabled = false;
            StopReceiver();
        }

        private void CheckBoxUseAppConfigCheckedChanged(object sender, EventArgs e)
        {
            _numericUpDownPort.Enabled = !_checkBoxUseAppConfig.Checked;
        }

        private void CheckBoxFilterLineCheckedChanged(object sender, EventArgs e)
        {
            _textBoxLineFullPath.Enabled = _checkBoxFilterLine.Checked;
        }

        private void ListBoxAdaptersSelectedIndexChanged(object sender, EventArgs e)
        {
            _buttonErrorCodes.Enabled = _buttonGetConfiguration.Enabled = _listBoxAdapters.SelectedItem != null;
        }

        private void ButtonGetConfigurationClick(object sender, EventArgs e)
        {
            try
            {
                string monitoringAdapterEndpoint = _listBoxAdapters.SelectedItem.ToString();
                EndpointAddress epa = new EndpointAddress(monitoringAdapterEndpoint);
                Monitoring monitoringInstance = MonitoringManager.GetOrCreateInstance(epa, EndpointHelper.CreateBindingFromEndpointString(monitoringAdapterEndpoint, true, true));
                Asm.As.Oib.Monitoring.Proxy.Business.Objects.Configuration config = monitoringInstance.GetConfiguration();
                AddMessage("Configuration: " + config);
            }
            catch (Exception ex)
            {
                AddMessage("ERROR: Got exception while calling GetConfiguration(): " + ex.Message);
            }
        }

        private void ButtonErrorCodesClick(object sender, EventArgs e)
        {

            #region DOC_MONITORING_READ_MACHINE_ERROR_STRINGS
            var saveFileDialog1 = new SaveFileDialog();

            try
            {
                string monitoringAdapterEndpoint = _listBoxAdapters.SelectedItem.ToString();
                EndpointAddress epa = new EndpointAddress(monitoringAdapterEndpoint);
                Monitoring monitoringInstance = MonitoringManager.GetOrCreateInstance(epa, EndpointHelper.CreateBindingFromEndpointString(monitoringAdapterEndpoint, true, true));

                var builder = new StringBuilder("MachineErrorCodes:\n");
                foreach (var kvp in monitoringInstance.MachineErrorStrings)
                {
                    builder.AppendFormatSafeLine("{0}: \t\t\tDescription: '{1}'", kvp.Key.ToString("D5"), kvp.Value);
                }
                saveFileDialog1.Filter = @"txt files (*.txt)|*.txt";
                saveFileDialog1.FileName = "ErrorStrings-" + _comboBoxLanguage.SelectedItem + ".txt";
                saveFileDialog1.FilterIndex = 2;
                saveFileDialog1.RestoreDirectory = true;

                if (saveFileDialog1.ShowDialog() == DialogResult.OK)
                {
                    System.IO.File.WriteAllText(saveFileDialog1.FileName, builder.ToString());
                }

            }
            catch (Exception ex)
            {
                AddMessage("ERROR: Got exception while saving File '" + saveFileDialog1.FileName + "', exception: " + ex.Message);
            }
            #endregion
        }

        private void ComboBoxLanguageSelectedIndexChanged(object sender, EventArgs e)
        {
            // Switch to whatever language you want. If the satellite assembly is installed in the sub-folder of where  the Monitoring Proxy is located, 
            // then you get translated descriptions.
            // Default language is English

            // You can only set this once, somehow... apparently the satellite assembly will not be loaded again once it is set.
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(_comboBoxLanguage.SelectedItem.ToString());
            _comboBoxLanguage.Enabled = false;
        }

        #endregion
        
        // PostgreSQL Connection String - Read from App.config connectionStrings
        private string PgConnectionString
        {
            get
            {
                // Find the first user-defined connection string (skipping the default LocalSqlServer)
                foreach (ConnectionStringSettings cs in ConfigurationManager.ConnectionStrings)
                {
                    if (cs.Name != "LocalSqlServer")
                    {
                        return cs.ConnectionString;
                    }
                }

                throw new InvalidOperationException(
                    "No database connection strings found in App.config. "
                    + "Please add a <connectionStrings> entry.");
            }
        }

        /// <summary>
        /// Truncates a string to maxLen characters to prevent DB VARCHAR overflow.
        /// </summary>
        private static string Truncate(string value, int maxLen = 500)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLen ? value : value.Substring(0, maxLen);
        }

        #region System Tray Methods

        private void InitializeSystemTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            _trayMenu.Items.Add("Exit", null, (s, e) => Application.Exit());

            _notifyIcon = new NotifyIcon
            {
                Text = "Monitoring Part Usage R23",
                Icon = this.Icon,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            _notifyIcon.MouseDoubleClick += (s, e) => RestoreFromTray();
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        }

        private void HideToTray()
        {
            this.Hide();
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(3000, "Monitoring Part Usage R23", "App is running in system tray.", ToolTipIcon.Info);
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            // _notifyIcon.Visible = false; // Keep it visible or hide? Usually keeping it visible is better.
        }

        #endregion
    }
}
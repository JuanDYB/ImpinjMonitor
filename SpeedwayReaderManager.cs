using Impinj.OctaneSdk;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WS.Reader.Procedure
{
    internal class SpeedwayReaderManager
    {
        private static NLog.Logger logger = LogManager.GetCurrentClassLogger();

        private const string READER_USERNAME = "root";
        private const string READER_PASSWORD = "impinj";

        //Properties
        private Reader ReaderInformation;
        private readonly ushort ReaderConnectTimeoutSecs;
        private readonly uint KeepAliveSecs;
        private readonly ushort MaxKeepAliveLost;
        private readonly ushort RebootAfterReconnects;

        //ImpinjAPI
        private ImpinjReader _impinjReader;

        //Estado
        internal bool Running { get; set; }
        internal bool IsConnected { get; set; }
        private System.Timers.Timer _checkNetworkTimer;
        private int ReconnectRetries = 0;

        //Inventario
        internal bool InventoryRunning { get; private set; }
        private IList<string> ReadedTags { get; set; }
        private static BlockingCollection<string> _tagInventoryThreadSafe;

        //GPIO
        private Dictionary<UInt16, Boolean> GPIOPortStatus = new Dictionary<UInt16, Boolean>();
        internal bool GPIOPortStatus_1
        {
            get { return this.GPIOPortStatus != null && this.GPIOPortStatus.ContainsKey(1) && this.GPIOPortStatus[1]; }
        }
        internal bool GPIOPortStatus_2
        {
            get { return this.GPIOPortStatus != null && this.GPIOPortStatus.ContainsKey(2) && this.GPIOPortStatus[2]; }
        }

        //Event Handlers
        internal event EventHandler<ReaderEventArgs> GPIEvent;
        internal event EventHandler<ReaderEventArgs> TagReportEvent;
        internal event EventHandler NewTagReadedEvent;
        internal event EventHandler<ReaderEventArgs> InventoryFinishEvent;

        public SpeedwayReaderManager(Reader readerInfo, ushort ReaderConnectTimeoutSecs, uint KeepAliveSecs, ushort MaxKeepAliveLost, ushort RebootAfterReconnects)
        {
            this.ReaderInformation = readerInfo;
            this.ReaderConnectTimeoutSecs = ReaderConnectTimeoutSecs;
            this.KeepAliveSecs = KeepAliveSecs;
            this.MaxKeepAliveLost = MaxKeepAliveLost;
            this.RebootAfterReconnects = RebootAfterReconnects;
            this.IsConnected = false;
            this._impinjReader = new ImpinjReader(this.ReaderInformation.IP, this.ReaderInformation.NAME);
        }

        #region Reader Connect

        internal void StartReader()
        {
            try
            {
                this.Running = true;
                this.ReaderConnect();
                this.InitReader();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[START READER ERROR]");
            }
        }

        /// <summary>
        /// Disconnect Reader and Quit events. END READER
        /// </summary>
        internal void EndReader()
        {
            try
            {
                this.Running = false;
                this.UnsetReaderEvents();
                this.ReaderDisconnect();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[END READER ERROR]");
            }
        }

        /// <summary>
        /// Connect to the reader
        /// </summary>
        private void ReaderConnect()
        {
            if (!this.IsConnected && this._impinjReader != null)
            {
                try
                {
                    logger.Info("[CONECTANDO CON READER]: {0}-{1}", this._impinjReader.Name, this._impinjReader.Address);
                    this._impinjReader.ConnectTimeout = this.ReaderConnectTimeoutSecs * 1000;
                    this._impinjReader.Connect();
                    this.IsConnected = true;
                    this.ReconnectRetries = 0;
                    logger.Info("[READER CONECTADO!!]: {0}-{1}", this._impinjReader.Name, this._impinjReader.Address);
                    this.CheckReaderStatus();
                    
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "[CONEXION FALLIDA CON READER]: {0}-{1}", this._impinjReader.Name, this._impinjReader.Address);
                    this.IsConnected = false;
                    this.ReconnectRetries++;
                    this.StartCheckNetwork();
                    throw;
                }
            }
        }

        /// <summary>
        /// Check reader status. Call this method after connect
        /// </summary>
        private void CheckReaderStatus()
        {
            Impinj.OctaneSdk.Status _readerStatus = this._impinjReader.QueryStatus();
            //Check if reader is in inventory and stops it
            if (!this.InventoryRunning && _readerStatus.IsSingulating)
            {
                logger.Warn("[INVENTORY STOPED WHILE READER WAS OFFLINE] Trying to stop");
                this.StopInventory();
            }

            //Resume reader events
            this._impinjReader.ResumeEventsAndReports();

            //Log Reader temperature
            logger.Info("READER TEMPERATURE: {0}ºC", _readerStatus.TemperatureInCelsius);

            //Log Antenna information
            foreach(Impinj.OctaneSdk.AntennaStatus _antStatus in _readerStatus.Antennas)
            {
                logger.Info("ANTENNA STATUS [PORT: {0} | CONNECTED: {1}]", _antStatus.PortNumber, _antStatus.IsConnected);
            }

            //Log GPI information
            foreach(Impinj.OctaneSdk.GpiStatus _gpiStatus in _readerStatus.Gpis)
            {
                logger.Info("GPI STATUS [PORT: {0} | STATUS: {1}]", _gpiStatus.PortNumber, _gpiStatus.State);
            }
        }

        /// <summary>
        /// Initialize Reader. Assign Events and configure parameters
        /// </summary>
        private void InitReader()
        {
            if (this.IsConnected && this._impinjReader != null)
            {
                try
                {
                    this.InitializeReaderParameters();
                    this.SetReaderEvents();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "[ERROR CONFIGURE READER]");
                    this.UnsetReaderEvents();
                    throw;
                }
            }
        }

        /// <summary>
        /// Reader Disconnect
        /// </summary>
        private void ReaderDisconnect()
        {
            logger.Info("[DESCONEXION]: {0}-{1}", this._impinjReader.Name, this._impinjReader.Address);
            this.IsConnected = false;
            try
            {
                this.StopCheckNetworkTimer();
                _impinjReader.Disconnect();
            }
            catch(Exception ex)
            {
                logger.Error(ex, "[ERROR DISCONNECT READER]");
                throw;
            }
        }

        private void ReconnectAfterConnectionLost()
        {
            try
            {
                if (!this.IsConnected && this.Running)
                {
                    if (this.Running)
                    {
                        this.ReaderConnect();
                    }
                    else
                    {
                        this.StartReader();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[ERROR RECONNECT READER]");
            }
        }

        #endregion


        #region Reader Configuration
        private void InitializeReaderParameters()
        {
            logger.Info("[CONFIGURANDO READER]");
            _impinjReader.DeleteAllOpSequences();

            // Get the default settings
            // We'll use these as a starting point
            // and then modify the settings we're interested in.
            Settings settings = _impinjReader.QueryDefaultSettings();
            var parameters = ReaderInformation.T_READER_PARAMETERS[0];

            // Set the start trigger to Immediate.
            // This will allow the reader to start as soon as it boots up.
            //settings.AutoStart.Mode = AutoStartMode.Immediate;

            // Tell the reader to include some paremeters
            // in all tag reports.
            settings.Report.IncludeAntennaPortNumber = true;
            settings.Report.IncludePeakRssi = true;
            settings.Report.IncludeFirstSeenTime = true;
            settings.Report.IncludeSeenCount = true;
            settings.Report.IncludePcBits = true;

            settings.Antennas.EnableAll();

            // Send a tag report for every tag read.
            settings.Report.Mode = ReportMode.Individual;

            settings.ReaderMode = ReaderMode.AutoSetDenseReader;
            settings.SearchMode = SearchMode.DualTarget;
            settings.Session = (ushort)parameters.SESSION_;

            settings.Antennas.EnableAll();
            for (ushort i = 1; i <= settings.Antennas.Length; i++)
            {
                if (settings.Antennas.GetAntenna(i).IsEnabled)
                {
                    // Set the Transmit Power and Receive Sensitivity
                    // settings.Antennas.GetAntenna(i).RxSensitivityInDbm = parameters.noise;
                    settings.Antennas.GetAntenna(i).TxPowerInDbm = (double)parameters.RFLEVEL / 100;
                }
            }
            //KeepAlive configuration
            KeepaliveConfig _keepaliveCfg = new KeepaliveConfig();
            _keepaliveCfg.Enabled = true;
            _keepaliveCfg.EnableLinkMonitorMode = true;
            _keepaliveCfg.PeriodInMs = this.KeepAliveSecs * 1000;
            _keepaliveCfg.LinkDownThreshold = this.MaxKeepAliveLost;
            settings.Keepalives = _keepaliveCfg;

            // If this application disconnects from the 
            // reader, hold all tag reports and events.
            settings.HoldReportsOnDisconnect = true;

            //No Autostop
            settings.AutoStop.Mode = AutoStopMode.None;

            // Apply the newly modified settings.
            _impinjReader.ApplySettings(settings);
        }

        private void SetReaderEvents()
        {
            logger.Info("[ASIGNACION EVENTOS READER]");
            //Eventos de conexion
            this._impinjReader.ConnectionLost += _impinjReader_ConnectionLost;
            this._impinjReader.KeepaliveReceived += _impinjReader_KeepaliveReceived;

            //Eventos de GPI
            this._impinjReader.GpiChanged += OnGPIChanged;
        }

        private void UnsetReaderEvents()
        {
            this._impinjReader.ConnectionLost -= _impinjReader_ConnectionLost;
            this._impinjReader.KeepaliveReceived -= _impinjReader_KeepaliveReceived;
            this._impinjReader.GpiChanged -= OnGPIChanged;
        }

        #endregion

        #region Reader Methods

        internal void StartInventory()
        {
            logger.Info("[INICIO INVENTARIO]");
            try
            {
                _tagInventoryThreadSafe = new BlockingCollection<string>();
                this.ReadedTags = new List<string>();
                this._impinjReader.TagsReported += OnTagsReported;
                this._impinjReader.Start();
                Task.Run(() => ProcessTagsTask(this));
                this.InventoryRunning = true;
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "ERROR INICIANDO INVENTARIO");
                this.InventoryRunning = false;
                this._impinjReader.TagsReported -= OnTagsReported;
                this.ReadedTags = null;
                _tagInventoryThreadSafe.CompleteAdding();
                _tagInventoryThreadSafe.Dispose();
                _tagInventoryThreadSafe = null;
                throw;
            }
        }

        internal void StopInventory()
        {
            logger.Info("[FIN DEL INVENTARIO]");
            
            try
            {
                this.InventoryRunning = false;
                if (_tagInventoryThreadSafe != null)
                {
                    _tagInventoryThreadSafe.CompleteAdding();
                }
                this._impinjReader.TagsReported -= OnTagsReported;
                this._impinjReader.Stop();
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "ERROR PARANDO INVENTARIO");
                this.InventoryRunning = false;
                throw;
            }
        }

        private static void ProcessTagsTask(SpeedwayReaderManager readerManager)
        {
            if(_tagInventoryThreadSafe != null)
            {
                foreach(string _tagEPC in _tagInventoryThreadSafe.GetConsumingEnumerable())
                {
                    if (!readerManager.ReadedTags.Contains(_tagEPC))
                    {
                        logger.Info("[UNIQUE TAG FOUND]: {0}", _tagEPC);
                        readerManager.ReadedTags.Add(_tagEPC);
                        readerManager.NewTagReadedEvent?.Invoke(readerManager, new EventArgs());
                    }
                }
                _tagInventoryThreadSafe.Dispose();
                _tagInventoryThreadSafe = null;
            }
            logger.Info("ProcessTagsTask FINISHED!!");
            readerManager.InventoryFinishEvent?.Invoke(readerManager, new ReaderEventArgs { ReaderInventoryEPC = readerManager.ReadedTags });
        }

        #endregion

        #region Reader Events
        private void _impinjReader_ConnectionLost(ImpinjReader reader)
        {
            logger.Error("[CONEXION PERDIDA]: {0}-{1}", reader.Name, reader.Address);
            try
            {
                this.ReaderDisconnect();
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "CONNECTION LOST. DISCONNECT ERROR");
            }
            this.StartCheckNetwork();
        }

        private void _impinjReader_KeepaliveReceived(ImpinjReader reader)
        {
            //logger.Trace("[KeepAliveReceived]: {0}-{1}", reader.Name, reader.Address);
        }

        private void OnGPIChanged(ImpinjReader reader, GpiEvent e)
        {
            this.SavePortSatus(e.PortNumber, e.State);
            ReaderEventArgs _evtArgs = new ReaderEventArgs { GPIPortNumber = e.PortNumber, GPIPortStatus = e.State };
            GPIEvent?.Invoke(this, _evtArgs);
        }

        private void OnTagsReported(ImpinjReader sender, TagReport report)
        {
            if (this.InventoryRunning)
            {
                foreach (Tag tag in report)
                {
                    string HexEpc = tag.Epc.ToHexString();
                    //logger.Trace("[TAG DETECTED]: {0} {1}", HexEpc, this.InventoryRunning);
                    try
                    {
                        if (!_tagInventoryThreadSafe.IsAddingCompleted)
                        {
                            _tagInventoryThreadSafe.Add(HexEpc);
                        }
                        else
                        {
                            logger.Warn("ADD TO BLOCK COLLECTION NOT POSSIBLE [{0}]", HexEpc);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "ERROR ADDING TAG TO BLOCKCOLLECTION [{0}]", HexEpc);
                    }
                }
            }
        }
        #endregion

        /// <summary>
        /// Save GPI Status on Dictionary on every Change
        /// </summary>
        /// <param name="PortNumber"></param>
        /// <param name="state"></param>
        private void SavePortSatus(ushort PortNumber, bool state)
        {
            if (this.GPIOPortStatus == null)
            {
                this.GPIOPortStatus = new Dictionary<ushort, bool>();
            }
            this.GPIOPortStatus[PortNumber] = state;
            logger.Info("GPIO Event[PUERTO: {0} ESTADO: {1}]", PortNumber, state);
        }

        #region Network Timer
        private void StartCheckNetwork()
        {
            this._checkNetworkTimer = new System.Timers.Timer();
            this._checkNetworkTimer.Interval = 2000;
            this._checkNetworkTimer.Enabled = true;
            this._checkNetworkTimer.AutoReset = true;
            this._checkNetworkTimer.Elapsed += _checkNetworkTimer_Elapsed; ;
            this._checkNetworkTimer.Start();
        }

        private void _checkNetworkTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            bool ChkNetwork = NetworkUtils.DoPing(this.ReaderInformation.IP);
            if (ChkNetwork)
            {
                this.StopCheckNetworkTimer();

                //Reboot if reconnects exceed
                if(this.RebootAfterReconnects > 0 && this.ReconnectRetries > this.RebootAfterReconnects)
                {
                    this.ReconnectRetries = 0;
                    this.SendRShellCommand("reboot");
                }
                this.ReconnectAfterConnectionLost();
            }
        }

        private void StopCheckNetworkTimer()
        {
            this._checkNetworkTimer.AutoReset = false;
            this._checkNetworkTimer.Enabled = false;
            this._checkNetworkTimer.Dispose();
        }
        #endregion

        #region RSHELL

        /// <summary>
        /// Send command to reader via Rshell
        /// </summary>
        /// <param name="command"></param>
        private void SendRShellCommand(string command)
        {
            try
            {
                logger.Info("SENDING RSHELL COMMAND [{0}]...", command);
                string reply = String.Empty; 
                this._impinjReader.RShell.OpenSecureSession(this.ReaderInformation.IP, READER_USERNAME, READER_PASSWORD, 5000);
                RShellCmdStatus status = this._impinjReader.RShell.Send(command, out reply);
                if (status == RShellCmdStatus.Success)
                {
                    logger.Info("RSHELL COMMAND SUCESSFUL [{0}]...", command);
                }
                else
                {
                    logger.Error("RSHELL COMMAND FAILED [{0}]...", command);
                }
                logger.Info("RSHELL COMMAND REPLY [{0}]...", reply);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ERROR SENDING RSHELL COMMAND [{0}]...", command);
            }
            finally
            {
                this._impinjReader.RShell.Close();
            }
        }

        #endregion
    }
}

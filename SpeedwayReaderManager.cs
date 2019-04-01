using Impinj.OctaneSdk;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WS.Reader.Procedure
{
    internal class SpeedwayReaderManager
    {
        private static NLog.Logger logger = LogManager.GetCurrentClassLogger();

        //Properties
        private Reader ReaderInformation;
        private readonly uint KeepAliveSecs;
        private readonly ushort MaxKeepAliveLost;

        //ImpinjAPI
        private ImpinjReader _impinj;

        //Estado
        internal bool Running { get; set; }
        internal bool IsConnected { get; set; }
        internal bool InventoryRunning;
        private System.Timers.Timer _checkNetworkTimer;

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

        public SpeedwayReaderManager(Reader readerInfo, uint KeepAliveSecs, ushort MaxKeepAliveLost)
        {
            this.ReaderInformation = readerInfo;
            this.KeepAliveSecs = KeepAliveSecs;
            this.MaxKeepAliveLost = MaxKeepAliveLost;
            this.IsConnected = false;
            this._impinj = new ImpinjReader(this.ReaderInformation.IP, this.ReaderInformation.NAME);
        }

        #region Reader Connect
        internal void Connect()
        {
            if (!this.IsConnected)
            {
                try
                {
                    logger.Info("[CONECTANDO CON READER]");
                    this._impinj.ConnectTimeout = 2000;
                    this._impinj.Connect();
                    this.InitializeReaderParameters();
                    this.SetReaderEvents();
                    this.IsConnected = true;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "[CONEXION FALLIDA CON READER]: {0}", this.ReaderInformation.IP);
                    this.IsConnected = false;
                    this.StartCheckNetwork();
                }
            }
            logger.Trace("FIN del Metodo Reader Connect");
        }

        internal void Disconnect()
        {
            this.Running = false;
            logger.Info("[DESCONEXION]: {0}", this.ReaderInformation.IP);
            if (this.InventoryRunning)
            {
                this.StopInventory();
            }
            this.UnsetReaderEvents();
            _impinj.Disconnect();
        }
        #endregion


        #region Reader Configuration
        private void InitializeReaderParameters()
        {
            _impinj.DeleteAllOpSequences();

            // Get the default settings
            // We'll use these as a starting point
            // and then modify the settings we're interested in.
            Settings settings = _impinj.QueryDefaultSettings();
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

            // Apply the newly modified settings.
            _impinj.ApplySettings(settings);
        }

        private void SetReaderEvents()
        {
            //Eventos de conexion
            this._impinj.ConnectionLost += new ImpinjReader.ConnectionLostHandler(Connection_lost);
            //this._impinj.KeepaliveReceived += new ImpinjReader.KeepaliveHandler(Connection_lost);

            //Eventos de GPI
            this._impinj.GpiChanged += OnGPIChanged;
        }

        private void UnsetReaderEvents()
        {
            this._impinj.ConnectionLost -= Connection_lost;
            this._impinj.GpiChanged -= OnGPIChanged;
        }

        #endregion

        #region Reader Methods

        internal void StartInventory()
        {
            logger.Info("[INICIO INVENTARIO]");
            try
            {
                this._impinj.TagsReported += OnTagsReported;
                this._impinj.Start();
                this.InventoryRunning = true;
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "ERROR INICIANDO INVENTARIO");
                throw;
            }
        }

        internal void StopInventory()
        {
            logger.Info("[FIN DEL INVENTARIO]");
            
            try
            {
                this._impinj.TagsReported -= OnTagsReported;
                this._impinj.Stop();
                this.InventoryRunning = false;
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "ERROR PARANDO INVENTARIO");
                throw;
            }
        }

        #endregion

        #region Reader Events
        private void Connection_lost(ImpinjReader reader)
        {
            logger.Error("[CONEXION PERDIDA]: {0}", this.ReaderInformation.IP);
            this.IsConnected = false;
            this.StartCheckNetwork();
        }

        private void OnGPIChanged(ImpinjReader reader, GpiEvent e)
        {
            ReaderEventArgs _evtArgs = new ReaderEventArgs { GPIPortNumber = e.PortNumber, GPIPortStatus = e.State };
            GPIEvent?.Invoke(this, _evtArgs);
        }

        private void OnTagsReported(ImpinjReader sender, TagReport report)
        {
            if (this.InventoryRunning)
            {
                List<string> ReadedTags = new List<string>();
                foreach (Tag tag in report)
                {
                    string HexEpc = tag.Epc.ToHexString();
                    if (!ReadedTags.Any(X => X == HexEpc))
                    {
                        ReadedTags.Add(HexEpc);
                        //this.LogMessage("Info", String.Format("[TAG DETECTED]: {0}", HexEpc));
                    }
                }
                if (ReadedTags.Any())
                {
                    GPIEvent?.Invoke(this, new ReaderEventArgs { ReaderInventoryEPC = ReadedTags });
                }
            }
        }
        #endregion

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
                this._checkNetworkTimer.AutoReset = false;
                this._checkNetworkTimer.Enabled = false;
                this._checkNetworkTimer.Dispose();
                if (!IsConnected)
                {
                    this.Connect();
                }
            }
        }
        #endregion
    }
}

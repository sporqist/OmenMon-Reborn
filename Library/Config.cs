  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Text;
using System.Xml;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library.Locale;

namespace OmenMon.Library {

    // Implements application-wide configuration settings look-up
    // This part only contains the implementing methods
    public static partial class Config {

#region Initialization
        // State flag
        public static bool IsInitialized { get; private set; }

        // Localization strings interface
        public static ILocale Locale;

        // Initializes the configuration class
        public static void Initialize() {

            // Only do it once
            if(!IsInitialized) {

                // Set the configuration file location
                // Note: also used by locale, so must happen before
                try {

                    // Establish where to look for the XML configuration file
                    FilePath = Path.ChangeExtension(AppFile, ".xml");

                } catch { }

                // Initialize the message system
                if(!LocaleInit("Override"))
                    App.Exit(Config.ExitStatus.ErrorLocale);

                // Load the configuration
                Load();

                // Done
                IsInitialized = true;

            }

        }

        // Initializes the locale system
        public static bool LocaleInit() {

            // Instantiate the localization system
            if((Locale = OmenMon.Library.Locale.Locale.Instance) == null) {

                // Show an error if failed
                App.Error("ErrLocaleNull");
                return false;

            }

            return true;

        }

        // Initializes the locale system and sets the language
        public static bool LocaleInit(string language) {

            // Instantiate the locale
            if(!LocaleInit())
                return false;

            // Set the application language
            Locale.SetLanguage(language);

            return true;

        }
#endregion

#region Configuration Retrieval
        // Retrieves a Boolean flag value from the XML configuration file
        private static bool GetBool(XmlDocument xml, string node, out bool value) {
            value = false;
            try {
                if(Conv.GetBool(xml.SelectSingleNode(node).InnerText, out value))
                    return true;
            } catch {  }
            return false;

        }

        // Retrieves a string value from the XML configuration file
        private static string GetString(XmlDocument xml, string node) {
            string value = "";
            try {
                value = xml.SelectSingleNode(node).InnerText;
            } catch {  }
            return (value == null ? "" : value);
        }

        // Retrieves a string value if the element exists. A missing element leaves the
        // setting at its default (GetString() returned "", which overwrote defaults such
        // as GpuPowerDefault = "Maximum" with an empty, unparseable value).
        private static bool TryGetString(XmlDocument xml, string node, out string value) {
            value = null;
            try {
                XmlNode n = xml.SelectSingleNode(node);
                if(n == null)
                    return false;
                value = n.InnerText ?? "";
                return true;
            } catch {
                return false;
            }
        }

        // Retrieves an integer within [min, max]; out-of-range or missing keeps the default
        private static bool GetWordInRange(XmlDocument xml, string node, int min, int max, out int value) {
            value = 0;
            ushort word;
            if(!GetWord(xml, node, out word) || word < min || word > max)
                return false;
            value = word;
            return true;
        }

        // Keys of the collection entries that loaded successfully from the file. On save,
        // an element is only removed if its entry loaded and has since been deleted in
        // the application; elements that failed to load, unknown elements and comments
        // are left alone.
        private static readonly HashSet<string> LoadedColorPresets = new HashSet<string>();
        private static readonly HashSet<string> LoadedFanPrograms = new HashSet<string>();
        private static readonly HashSet<string> LoadedSensors = new HashSet<string>();
        private static readonly HashSet<string> LoadedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Retrieves an unsigned word-sized value from the XML configuration file
        private static bool GetWord(XmlDocument xml, string node, out ushort value) {
            value = 0;
            try {
                if(Conv.GetWord(xml.SelectSingleNode(node).InnerText, out value))
                    return true;
            } catch {  }
            return false;

        }

        // Loads the configuration data from the XML file
        public static void Load() {

            // Proceed only if the file exists
            if(FilePath != "" && File.Exists(FilePath)) {

                try {

                    // Load the file
                    XmlDocument xml = new XmlDocument();
                    xml.Load(FilePath);

                    // Replace the hard-coded XML template with a localized one
                    // Only possible once the localizable message class is instantiated
                    XmlTemplate = Config.Locale.Get("_ConfigXmlTemplate");

                    // Read the configuration and parse it into values
                    bool flag;
                    ushort value;

                    if(GetBool(xml, XmlPrefix + "AutoConfig", out flag))
                        AutoConfig = flag;

                    if(GetBool(xml, XmlPrefix + "AutoStartup", out flag))
                        AutoStartup = flag;

                    if(GetBool(xml, XmlPrefix + "BiosErrorReporting", out flag))
                        BiosErrorReporting = flag;

                    if(GetBool(xml, XmlPrefix + "BiosHeartbeatPauseOnBattery", out flag))
                        BiosHeartbeatPauseOnBattery = flag;

                    if(GetBool(xml, XmlPrefix + "BatteryGlitchGuard", out flag))
                        BatteryGlitchGuard = flag;

                    // Battery-glitch guard tuning knobs. Bounds picked so a bad XML edit
                    // can't break the guard's invariants. All three are loaded via GetWord
                    // (ushort, max 65535), so the documented ceilings must fit in ushort:
                    //   DropPercent  ∈ [1, 100]   (0 would fire on any tick)
                    //   WindowMs     ∈ [1, 60000] (longer than 1 minute exceeds the time
                    //                              an AC drain physically takes to cross
                    //                              the drop threshold, so any longer is
                    //                              pointless)
                    //   HoldMs       ∈ [1, 60000] (1-minute upper bound; anything longer
                    //                              is really a power-plan change, not a
                    //                              transient-glitch suppression)
                    if(GetWord(xml, XmlPrefix + "BatteryGlitchDropPercent", out value) && value >= 1 && value <= 100)
                        BatteryGlitchDropPercent = value;

                    if(GetWord(xml, XmlPrefix + "BatteryGlitchWindowMs", out value) && value >= 1 && value <= 60000)
                        BatteryGlitchWindowMs = value;

                    if(GetWord(xml, XmlPrefix + "BatteryGlitchHoldMs", out value) && value >= 1 && value <= 60000)
                        BatteryGlitchHoldMs = value;

                    if(GetBool(xml, XmlPrefix + "BatteryGlitchGuardOnBattery", out flag))
                        BatteryGlitchGuardOnBattery = flag;

                    if(GetBool(xml, XmlPrefix + "BatteryGlitchGuardDisableTimeout", out flag))
                        BatteryGlitchGuardDisableTimeout = flag;

                    if(GetBool(xml, XmlPrefix + "BatteryGlitchGuardHoldAlways", out flag))
                        BatteryGlitchGuardHoldAlways = flag;

                    if(GetBool(xml, XmlPrefix + "DisplayOffKeepAwake", out flag))
                        DisplayOffKeepAwake = flag;

                    if(GetBool(xml, XmlPrefix + "FanDataPipeServer", out flag))
                        FanDataPipeServer = flag;

                    if(GetBool(xml, XmlPrefix + "AcFlickerGuard", out flag))
                        AcFlickerGuard = flag;

                    // AC-flicker hold window. 0 disables (immediate switch, pre-v1.4.2
                    // behaviour); upper bound 60000 ms keeps it short enough that a real
                    // unplug always reaches the fan-program switch within one minute.
                    if(GetWord(xml, XmlPrefix + "AcFlickerHoldMs", out value) && value <= 60000)
                        AcFlickerHoldMs = value;

                    // Confirmation-sample count at the end of the debounce. Lower bound 1
                    // disables multi-sampling (single read, old behaviour); upper bound 20
                    // keeps the total worst-case latency at ConfirmIntervalMs * 20 = 5 s
                    // beyond AcFlickerHoldMs, still well below AcFlickerMaxDeferralMs.
                    if(GetWord(xml, XmlPrefix + "AcFlickerConfirmSamples", out value) && value >= 1 && value <= 20)
                        AcFlickerConfirmSamples = value;

                    // Per-sample interval inside the confirmation window. NOTE:
                    // confirmation samples are taken on the GUI timer tick
                    // (GuiTimerInterval, 1 s), so this acts as a lower bound quantized
                    // up to that cadence — values below ~1 s yield one sample per tick.
                    // Lower bound 10 ms matches the practical limit of
                    // SystemInformation.PowerStatus caching; upper bound 2000 ms keeps
                    // the worst-case confirmation latency bounded.
                    if(GetWord(xml, XmlPrefix + "AcFlickerConfirmIntervalMs", out value) && value >= 10 && value <= 2000)
                        AcFlickerConfirmIntervalMs = value;

                    // Safety ceiling on cascaded re-deferrals when confirmation keeps
                    // failing. Lower bound 1 ms preserves the permissive behaviour (a
                    // value below AcFlickerHoldMs just makes the cascade bypass the
                    // confirmation gate on the first post-hold tick — harmless); upper
                    // bound 1 minute (60000 ms) — beyond that a flapper is the new
                    // normal and the user wants the fan-program switch to win.
                    if(GetWord(xml, XmlPrefix + "AcFlickerMaxDeferralMs", out value) && value >= 1 && value <= 60000)
                        AcFlickerMaxDeferralMs = value;

                    if(GetBool(xml, XmlPrefix + "AcFlickerPassivePoll", out flag))
                        AcFlickerPassivePoll = flag;

                    if(GetBool(xml, XmlPrefix + "ThermalPanicEnabled", out flag))
                        ThermalPanicEnabled = flag;

                    if(GetWord(xml, XmlPrefix + "ThermalPanicTemperature", out value) && value <= byte.MaxValue)
                        ThermalPanicTemperature = (byte) value;

                    if(GetWord(xml, XmlPrefix + "ThermalPanicHysteresis", out value) && value <= byte.MaxValue)
                        ThermalPanicHysteresis = (byte) value;

                    // Disable Thermal Panic when configured values are nonsensical:
                    // Temperature=0 would trigger immediately; Hysteresis>=Temperature would
                    // leave panic permanently active (off-threshold underflows to 0 or below).
                    if(ThermalPanicTemperature < 1 || ThermalPanicHysteresis >= ThermalPanicTemperature)
                        ThermalPanicEnabled = false;

                    if(GetBool(xml, XmlPrefix + "TemperatureUseFahrenheit", out flag))
                        TemperatureUseFahrenheit = flag;

                    int number;

                    if(GetWord(xml, XmlPrefix + "EcFailLimit", out value))
                        EcFailLimit = value;

                    // EC tuning: only values that would disable EC access outright are
                    // rejected (0 retries, 0 wait iterations, 0 ms mutex timeout); the
                    // transaction code itself is unchanged
                    if(GetWordInRange(xml, XmlPrefix + "EcMonInterval", 100, 60000, out number))
                        EcMonInterval = number;

                    if(GetWordInRange(xml, XmlPrefix + "EcMutexTimeout", 1, 60000, out number))
                        EcMutexTimeout = number;

                    if(GetWordInRange(xml, XmlPrefix + "EcRetryLimit", 1, 100, out number))
                        EcRetryLimit = number;

                    if(GetWordInRange(xml, XmlPrefix + "EcWaitLimit", 1, 10000, out number))
                        EcWaitLimit = number;

                    if(GetWord(xml, XmlPrefix + "EcWaitSpinCount", out value))
                        EcWaitSpinCount = value;

                    if(GetWord(xml, XmlPrefix + "EcWaitYieldCount", out value))
                        EcWaitYieldCount = value;

                    if(GetBool(xml, XmlPrefix + "FanCountdownExtendAlways", out flag))
                        FanCountdownExtendAlways = flag;

                    // The countdown is a one-byte EC register [s]
                    if(GetWordInRange(xml, XmlPrefix + "FanCountdownExtendInterval", 10, 255, out number))
                        FanCountdownExtendInterval = number;

                    if(GetWordInRange(xml, XmlPrefix + "FanCountdownExtendThreshold", 0, 120, out number))
                        FanCountdownExtendThreshold = number;

                    // Fan levels [rpm / 100]; a minimum above the maximum keeps both defaults
                    int levelMax = FanLevelMax, levelMin = FanLevelMin;
                    if(GetWordInRange(xml, XmlPrefix + "FanLevelMax", 10, 100, out number))
                        levelMax = number;
                    if(GetWordInRange(xml, XmlPrefix + "FanLevelMin", 0, 100, out number))
                        levelMin = number;
                    if(levelMin < levelMax) {
                        FanLevelMax = levelMax;
                        FanLevelMin = levelMin;
                    }

                    // Fan safety: floors below 10 (1000 rpm) would allow stopped or stalling
                    // fans while warm and are rejected, as are cool thresholds outside
                    // 30-70 °C and Fan Off limits outside 1-240 min (defaults are kept)
                    if(GetWord(xml, XmlPrefix + "FanSafetyFloorCpu", out value) && value >= 10 && value <= 100)
                        FanSafetyFloorCpu = (byte) value;

                    if(GetWord(xml, XmlPrefix + "FanSafetyFloorGpu", out value) && value >= 10 && value <= 100)
                        FanSafetyFloorGpu = (byte) value;

                    if(GetWord(xml, XmlPrefix + "FanSafetyCoolTemperature", out value) && value >= 30 && value <= 70)
                        FanSafetyCoolTemperature = value;

                    if(GetWord(xml, XmlPrefix + "FanOffMaxMinutes", out value) && value >= 1 && value <= 240)
                        FanOffMaxMinutes = value;

                    if(TryGetString(xml, XmlPrefix + "FanSafetyWarmCurve", out string warmCurve)
                        && OmenMon.Hardware.Platform.SafetyStep.ParseList(warmCurve) != null)
                        FanSafetyWarmCurve = warmCurve;

                    if(GetBool(xml, XmlPrefix + "FanLevelNeedManual", out flag))
                        FanLevelNeedManual = flag;

                    if(GetBool(xml, XmlPrefix + "FanLevelUseEc", out flag))
                        FanLevelUseEc = flag;

                    string text;

                    if(TryGetString(xml, XmlPrefix + "FanProgramDefault", out text))
                        FanProgramDefault = text;

                    if(TryGetString(xml, XmlPrefix + "FanProgramDefaultAlt", out text))
                        FanProgramDefaultAlt = text;

                    string customFanMode = GetString(xml, "//OmenMon/CustomPreset/Mode");
                    if(!string.IsNullOrEmpty(customFanMode))
                        CustomPresetFanMode = customFanMode;

                    string customGpuPower = GetString(xml, "//OmenMon/CustomPreset/GpuPower");
                    if(!string.IsNullOrEmpty(customGpuPower))
                        CustomPresetGpuPower = customGpuPower;

                    if(GetWord(xml, "//OmenMon/CustomPreset/MaxRpm", out value))
                        CustomPresetMaxRpm = value;

                    if(GetWord(xml, "//OmenMon/CustomPreset/Fan0Level", out value) && value <= 100)
                        CustomPresetCpuLevel = (byte) value;

                    if(GetWord(xml, "//OmenMon/CustomPreset/Fan1Level", out value) && value <= 100)
                        CustomPresetGpuLevel = (byte) value;

                    if(GetBool(xml, XmlPrefix + "FanProgramModeCheckFirst", out flag))
                        FanProgramModeCheckFirst = flag;

                    if(GetBool(xml, XmlPrefix + "FanProgramSharedTemperature", out flag))
                        FanProgramSharedTemperature = flag;

                    if(GetBool(xml, XmlPrefix + "FanProgramSuspend", out flag))
                        FanProgramSuspend = flag;

                    // Must name a GPU power level: AutoConfig parses it
                    if(TryGetString(xml, XmlPrefix + "GpuPowerDefault", out text)
                        && Enum.TryParse(text, false, out BiosData.GpuPowerLevel gpuPowerLevel)
                        && Enum.IsDefined(typeof(BiosData.GpuPowerLevel), gpuPowerLevel))
                        GpuPowerDefault = text;

                    if(GetWordInRange(xml, XmlPrefix + "GpuPowerSetInterval", 0, 5000, out number))
                        GpuPowerSetInterval = number;

                    if(GetBool(xml, XmlPrefix + "GuiCloseWindowExit", out flag))
                        GuiCloseWindowExit = flag;

                    if(GetBool(xml, XmlPrefix + "GuiDpiChangeResize", out flag))
                        GuiDpiChangeResize = flag;

                    if(GetBool(xml, XmlPrefix + "GuiDynamicIcon", out flag))
                        GuiDynamicIcon = flag;

                    if(GetBool(xml, XmlPrefix + "GuiDynamicIconHasBackground", out flag))
                        GuiDynamicIconHasBackground = flag;

                    if(GetBool(xml, XmlPrefix + "GuiStayOnTop", out flag))
                        GuiStayOnTop = flag;

                    // 0 = default size
                    if(GetWordInRange(xml, XmlPrefix + "GuiSysInfoFontSize", 0, 72, out number)
                        && (number == 0 || number >= 6))
                        GuiSysInfoFontSize = number;

                    if(GetWord(xml, XmlPrefix + "GuiTipDuration", out value))
                        GuiTipDuration = value;

                    if(GetBool(xml, XmlPrefix + "KeyToggleColorPreset", out flag))
                        KeyToggleColorPreset = flag;

                    if(GetBool(xml, XmlPrefix + "KeyToggleColorPresetSilent", out flag))
                        KeyToggleColorPresetSilent = flag;

                    if(GetBool(xml, XmlPrefix + "KeyToggleFanProgram", out flag))
                        KeyToggleFanProgram = flag;

                    if(GetBool(xml, XmlPrefix + "KeyToggleFanProgramCycleAll", out flag))
                        KeyToggleFanProgramCycleAll = flag;

                    if(GetBool(xml, XmlPrefix + "KeyToggleFanProgramShowGuiFirst", out flag))
                        KeyToggleFanProgramShowGuiFirst = flag;

                    if(GetBool(xml, XmlPrefix + "KeyToggleFanProgramSilent", out flag))
                        KeyToggleFanProgramSilent = flag;

                    if(GetWordInRange(xml, XmlPrefix + "PresetRefreshRateHigh", 24, 1000, out number))
                        PresetRefreshRateHigh = number;

                    if(GetWordInRange(xml, XmlPrefix + "PresetRefreshRateLow", 24, 1000, out number))
                        PresetRefreshRateLow = number;

                    // Intervals in GUI timer ticks (1 s): 0 would run every tick, and the
                    // fan program interval must stay below the ~124 s a BIOS fan level lives
                    if(GetWordInRange(xml, XmlPrefix + "UpdateIconInterval", 1, 3600, out number))
                        UpdateIconInterval = number;

                    if(GetWordInRange(xml, XmlPrefix + "UpdateMonitorInterval", 1, 3600, out number))
                        UpdateMonitorInterval = number;

                    if(GetWordInRange(xml, XmlPrefix + "UpdateProgramInterval", 1, 60, out number))
                        UpdateProgramInterval = number;

                    // Load the key custom action settings
                    if(GetBool(xml, XmlPrefixKeyCustomAction + "Enabled", out flag))
                        KeyCustomActionEnabled = flag;

                    if(TryGetString(xml, XmlPrefixKeyCustomAction + "ExecCmd", out text))
                        KeyCustomActionExecCmd = text;

                    if(TryGetString(xml, XmlPrefixKeyCustomAction + "ExecArgs", out text))
                        KeyCustomActionExecArgs = text;

                    if(GetBool(xml, XmlPrefixKeyCustomAction + "Minimized", out flag))
                        KeyCustomActionMinimized = flag;

                    // Load the color presets
                    SortedDictionary<string, BiosData.ColorTable> ColorPresetXml
                        = new SortedDictionary<string, BiosData.ColorTable>();
                    LoadedColorPresets.Clear();
                    foreach(XmlNode node in xml.SelectNodes(XmlPrefixColorPreset)) {
                        // Invalid entries are not loaded (but stay in the file)
                        string name;
                        BiosData.ColorTable colorTable;
                        if(TryParseColorPreset(node, out name, out colorTable)) {
                            ColorPresetXml[name] = colorTable;
                            LoadedColorPresets.Add(name);
                        }
                    }

                    // Replace the defaults with configured color presets unless none
                    if(ColorPresetXml.Count > 0)
                        ColorPreset = ColorPresetXml;

                    // Populate the RTF header with colors at run-time
                    SysInfoRtfHeader = SysInfoRtfPreHeader + 
                        "{\\colortbl;"
                        + Conv.GetColorStringRtf(SystemColors.GrayText.ToArgb())  // System Gray
                        + Conv.GetColorStringRtf(0)                               // Black
                        + Conv.GetColorStringRtf(GuiColorTextTeal)                // Teal
                        + Conv.GetColorStringRtf(GuiColorWarmDark)                // Red
                        + Conv.GetColorStringRtf(GuiColorTextBlue)                // Blue
                        + Conv.GetColorStringRtf(GuiColorWarmLite)                // Fuchsia
                        + "}";

                    // Load the temperature sensors
                    bool usable = false;
                    Dictionary<string, TemperatureSensorData> TemperatureSensorXml
                        = new Dictionary<string, TemperatureSensorData>();
                    HashSet<string> sensorsXml = new HashSet<string>();
                    foreach(XmlNode node in xml.SelectNodes(XmlPrefixTemperatureSensor)) {

                        // Abort if more than the maximum number of sensors defined already
                        if(TemperatureSensorXml.Count >= TemperatureSensorMax)
                            break;

                        // Invalid entries are not loaded (but stay in the file)
                        string name;
                        TemperatureSensorData sensor;
                        if(TryParseSensor(node, out name, out sensor)) {
                            TemperatureSensorXml[name] = sensor;
                            sensorsXml.Add(name);
                            if(sensor.Use) usable = true;
                        }

                    }

                    // Replace the defaults with configured temperature sensors unless none
                    // were configured or not a single sensor was set to actually be used
                    LoadedSensors.Clear();
                    if(TemperatureSensorXml.Count > 0 && usable) {
                        TemperatureSensor = TemperatureSensorXml;
                        LoadedSensors.UnionWith(sensorsXml);
                    }

                    // Load the model database entries
                    LoadedModels.Clear();
                    foreach(XmlNode node in xml.SelectNodes(XmlPrefixModel)) {
                        OmenMon.Hardware.Platform.PlatformPreset p;
                        if(TryParseModel(node, out p)) {
                            Models[p.ProductId] = p;
                            LoadedModels.Add(p.ProductId);
                        }
                    }

                    // Load the fan programs
                    LoadedFanPrograms.Clear();
                    foreach(XmlNode node in xml.SelectNodes(XmlPrefixFanProgram)) {
                        // Invalid entries are not loaded (but stay in the file)
                        FanProgramData program;
                        if(TryParseFanProgram(node, out program)) {
                            FanProgram[program.Name] = program;
                            LoadedFanPrograms.Add(program.Name);
                        }
                    }

                } catch {

                    // Silently ignore any errors

                }

            }

        }

        // Parses a color preset element
        private static bool TryParseColorPreset(XmlNode node, out string name, out BiosData.ColorTable colorTable) {
            name = null;
            colorTable = default(BiosData.ColorTable);
            try {
                name = node.Attributes[XmlAttrColorPresetName].Value;
                colorTable = new BiosData.ColorTable(node.InnerText);
                return !string.IsNullOrEmpty(name);
            } catch {
                return false;
            }
        }

        // Parses a temperature sensor element
        private static bool TryParseSensor(XmlNode node, out string name, out TemperatureSensorData sensor) {
            name = null;
            sensor = default(TemperatureSensorData);
            try {

                // Set the optional use flag
                // based on the XML attribute
                bool use = true;
                try {
                    Conv.GetBool(node.Attributes[XmlAttrTemperatureSensorUse].Value, out use);
                } catch {  }

                string source = node.Attributes[XmlAttrTemperatureSensorSource].Value;

                // Check for Embedded Controller sensor source
                if(source == XmlAttrTemperatureSensorSourceValueEc) {
                    name = node.Attributes[XmlAttrTemperatureSensorName].Value;
                    sensor = new TemperatureSensorData(
                        PlatformData.LinkType.EmbeddedController,
                        (byte) Enum.Parse(typeof(EmbeddedControllerData.Register), name), use);
                    return true;
                }

                // Check for WMI BIOS sensor source
                if(source == XmlAttrTemperatureSensorSourceValueBios) {
                    name = XmlAttrTemperatureSensorSourceValueBios;
                    sensor = new TemperatureSensorData(PlatformData.LinkType.WmiBios, use);
                    return true;
                }

            } catch { }
            return false;
        }

        // Parses a model database element
        private static bool TryParseModel(XmlNode node, out OmenMon.Hardware.Platform.PlatformPreset p) {
            p = null;
            try {
                string productId = node.Attributes[XmlAttrModelProductId].Value;
                if(string.IsNullOrEmpty(productId))
                    return false;
                string displayName = "";
                try { displayName = node.Attributes[XmlAttrModelDisplayName].Value; } catch { }
                p = new OmenMon.Hardware.Platform.PlatformPreset();
                p.ProductId        = productId;
                p.DisplayName      = displayName;
                p.FanLevelReg0     = Conv.GetByte(node[XmlElementFanLevelReg0].InnerText);
                p.FanLevelReg1     = Conv.GetByte(node[XmlElementFanLevelReg1].InnerText);
                p.FanRateReadReg0  = Conv.GetByte(node[XmlElementFanRateReadReg0].InnerText);
                p.FanRateReadReg1  = Conv.GetByte(node[XmlElementFanRateReadReg1].InnerText);
                p.FanRateWriteReg0 = Conv.GetByte(node[XmlElementFanRateWriteReg0].InnerText);
                p.FanRateWriteReg1 = Conv.GetByte(node[XmlElementFanRateWriteReg1].InnerText);
                p.FanSpeedReg0     = Conv.GetByte(node[XmlElementFanSpeedReg0].InnerText);
                p.FanSpeedReg1     = Conv.GetByte(node[XmlElementFanSpeedReg1].InnerText);
                p.CountdownReg     = Conv.GetByte(node[XmlElementCountdownReg].InnerText);
                p.ManualReg        = Conv.GetByte(node[XmlElementManualReg].InnerText);
                p.ModeReg          = Conv.GetByte(node[XmlElementModeReg].InnerText);
                p.SwitchReg        = Conv.GetByte(node[XmlElementSwitchReg].InnerText);
                // Optional per-model manual-trigger overrides (default FanManual.On/.Off).
                // Boards that gate manual fan control with a different value pair
                // (e.g. 8BBE — Victus 16 R0053NT — writes 0x08 to EC[0x06] to engage,
                // 0x48 to release) declare these explicitly; absent elements fall
                // back to the standard OMCC pair set on the preset's defaults.
                if(node[XmlElementManualValueOn] != null)
                    p.ManualValueOn = Conv.GetByte(node[XmlElementManualValueOn].InnerText);
                if(node[XmlElementManualValueOff] != null)
                    p.ManualValueOff = Conv.GetByte(node[XmlElementManualValueOff].InnerText);
                // Optional per-model temperature-sensor overrides — remap the
                // named CPUT / GPTM sensors to a different EC offset than the
                // global <Temperature> config. Used by boards that moved the
                // real CPU/GPU temp sensors away from legacy 0x57/0xB7 (e.g.
                // 8C9C exposes CPU at 0xB0 and GPU at 0xB4). Absent / 0 =
                // no override.
                if(node[XmlElementTempCpuReg] != null)
                    p.TempCpuReg = Conv.GetByte(node[XmlElementTempCpuReg].InnerText);
                if(node[XmlElementTempGpuReg] != null)
                    p.TempGpuReg = Conv.GetByte(node[XmlElementTempGpuReg].InnerText);
                return true;
            } catch {
                p = null;
                return false;
            }
        }

        // Parses a fan program element
        private static bool TryParseFanProgram(XmlNode node, out FanProgramData program) {
            program = null;
            try {

                // Set up a variable to read the level configuration into
                SortedDictionary<byte, byte[]> levels =
                    new SortedDictionary<byte, byte[]>();

                // Iterate through the levels specified in the XML file
                foreach(XmlNode subnode in node.SelectNodes(XmlElementFanProgramLevel)) {

                    // Populate the level data
                    levels[Conv.GetByte(
                        subnode.Attributes[XmlAttrFanProgramLevelTemperature].Value)] =
                            new byte[] {
                                Conv.GetByte(subnode[XmlElementFanProgramLevelCpu].InnerText),
                                Conv.GetByte(subnode[XmlElementFanProgramLevelGpu].InnerText)};

                }

                // A program without levels cannot run (FanProgram indexes its rows)
                if(levels.Count == 0)
                    return false;

                string name = node.Attributes[XmlAttrFanProgramName].Value;
                if(string.IsNullOrEmpty(name))
                    return false;

                // Create a new fan program from the configuration data
                program = new FanProgramData(
                    name,
                    ParseFanModeOrThrow(node[XmlElementFanProgramMode].InnerText),
                    (BiosData.GpuPowerLevel) Enum.Parse(typeof(BiosData.GpuPowerLevel), node[XmlElementFanProgramPower].InnerText),
                    levels);
                return true;

            } catch {
                return false;
            }
        }

        // Parses a program's BIOS fan mode, accepting defined values only
        private static BiosData.FanMode ParseFanModeOrThrow(string name) {
            BiosData.FanMode mode;
            if(!BiosData.TryParseFanMode(name, out mode))
                throw new ArgumentOutOfRangeException(nameof(name), name, "Not a BIOS fan mode");
            return mode;
        }

        // Exports a single fan program to a standalone XML file
        public static bool ExportFanProgram(string name, string filePath) {
            if(!FanProgram.ContainsKey(name)) return false;
            try {
                var xml = new System.Xml.XmlDocument();
                xml.AppendChild(xml.CreateXmlDeclaration("1.0", "utf-8", null));
                var root = xml.CreateElement("OmenMonFanProfile");
                xml.AppendChild(root);
                var prog = xml.CreateElement(XmlElementFanProgram);
                prog.SetAttribute(XmlAttrFanProgramName, name);
                root.AppendChild(prog);
                prog.AppendChild(xml.CreateElement(XmlElementFanProgramMode)).InnerText =
                    Enum.GetName(typeof(OmenMon.Hardware.Bios.BiosData.FanMode), FanProgram[name].FanMode);
                prog.AppendChild(xml.CreateElement(XmlElementFanProgramPower)).InnerText =
                    Enum.GetName(typeof(OmenMon.Hardware.Bios.BiosData.GpuPowerLevel), FanProgram[name].GpuPower);
                foreach(var temp in FanProgram[name].Level.Keys) {
                    var level = (System.Xml.XmlElement) prog.AppendChild(xml.CreateElement(XmlElementFanProgramLevel));
                    level.SetAttribute(XmlAttrFanProgramLevelTemperature, Conv.GetString(temp, 2, 10));
                    level.AppendChild(xml.CreateElement(XmlElementFanProgramLevelCpu)).InnerText =
                        Conv.GetString(FanProgram[name].Level[temp][0], 2, 10);
                    level.AppendChild(xml.CreateElement(XmlElementFanProgramLevelGpu)).InnerText =
                        Conv.GetString(FanProgram[name].Level[temp][1], 2, 10);
                }
                var settings = new System.Xml.XmlWriterSettings {
                    Indent = true, IndentChars = "    ", Encoding = new System.Text.UTF8Encoding(false)
                };
                using var writer = System.Xml.XmlWriter.Create(filePath, settings);
                xml.Save(writer);
                return true;
            } catch { return false; }
        }

        // Imports a fan program from a standalone XML file; returns the program name or null on failure
        public static string ImportFanProgram(string filePath) {
            try {
                // Disable DTD/external-entity processing to prevent XXE attacks
                var readerSettings = new System.Xml.XmlReaderSettings {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    XmlResolver  = null
                };
                var xml = new System.Xml.XmlDocument { XmlResolver = null };
                using(var reader = System.Xml.XmlReader.Create(filePath, readerSettings))
                    xml.Load(reader);

                // Accept both <OmenMonFanProfile> root and bare <Program> root
                var progNode = xml.SelectSingleNode("//" + XmlElementFanProgram) as System.Xml.XmlElement;
                if(progNode == null) return null;
                string name = progNode.GetAttribute(XmlAttrFanProgramName);
                if(string.IsNullOrEmpty(name)) return null;
                // Parse exactly like Load()
                var fanModeNode  = progNode.SelectSingleNode(XmlElementFanProgramMode);
                var gpuPowerNode = progNode.SelectSingleNode(XmlElementFanProgramPower);
                if(fanModeNode == null || gpuPowerNode == null) return null;
                if(!BiosData.TryParseFanMode(fanModeNode.InnerText, out BiosData.FanMode fanMode)) return null;
                if(!Enum.TryParse(gpuPowerNode.InnerText, out OmenMon.Hardware.Bios.BiosData.GpuPowerLevel gpuPower)) return null;
                var levels = new System.Collections.Generic.SortedDictionary<byte, byte[]>();
                foreach(System.Xml.XmlElement levelNode in progNode.SelectNodes(XmlElementFanProgramLevel)) {
                    if(!Conv.GetByte(levelNode.GetAttribute(XmlAttrFanProgramLevelTemperature), out byte temp)) continue;
                    var cpuNode = levelNode.SelectSingleNode(XmlElementFanProgramLevelCpu);
                    var gpuNode = levelNode.SelectSingleNode(XmlElementFanProgramLevelGpu);
                    if(cpuNode == null || gpuNode == null) continue;
                    if(!Conv.GetByte(cpuNode.InnerText, out byte cpu)) continue;
                    if(!Conv.GetByte(gpuNode.InnerText, out byte gpu)) continue;
                    levels[temp] = new byte[] { cpu, gpu };
                }
                if(levels.Count == 0) return null;

                // Avoid silently overwriting an existing curve — append numeric suffix until unique
                string finalName = name;
                int suffix = 1;
                while(FanProgram.ContainsKey(finalName))
                    finalName = name + " (" + (suffix++) + ")";

                FanProgram[finalName] = new OmenMon.Hardware.Platform.FanProgramData(finalName, fanMode, gpuPower, levels);
                return finalName;
            } catch { return null; }
        }
#endregion

#region Configuration Saving
        // Saves the configuration data to the XML file
        public static void Save() {
            try {
                SaveCore();
            } catch {

                // Show an error message if the settings could not be saved
                App.Error("ErrConfigSave");

            }
        }

        // Saves the configuration data to the XML file, throwing on failure
        //
        // The existing file is updated, not regenerated: scalar values are set in place,
        // and the collections (color presets, fan programs, temperature sensors, models)
        // are merged by key. An entry is rewritten only if its value changed, new entries
        // are appended, and an element is removed only if it loaded successfully and has
        // since been deleted in the application. Comments, unknown elements and entries
        // that failed to load stay as they are. The file is written to a temporary file
        // first and then swapped in, so an interrupted save cannot leave it truncated.
        // An existing file that cannot be parsed is kept as a backup copy before a new
        // one is written from the template.
        internal static void SaveCore() {

            // Proceed only if the filename is not empty
            if(FilePath == "")
                return;

            // Create a new XML document
            XmlDocument xml = new XmlDocument();

            if(File.Exists(FilePath)) {
                try {

                    // Load the existing configuration file
                    xml.Load(FilePath);

                } catch(XmlException) {

                    // Keep the unreadable file, then start from the template
                    File.Copy(FilePath, FilePath + ".unreadable-"
                        + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak", true);
                    xml = new XmlDocument();
                    xml.LoadXml(Config.XmlTemplate);

                }
            } else

                // Start with a pre-defined template
                xml.LoadXml(Config.XmlTemplate);

            // Create or update the configuration values
            SetBool(xml, XmlPrefix + "AutoConfig", AutoConfig);
            SetBool(xml, XmlPrefix + "AutoStartup", AutoStartup);
            SetBool(xml, XmlPrefix + "BiosErrorReporting", BiosErrorReporting);
            SetBool(xml, XmlPrefix + "BiosHeartbeatPauseOnBattery", BiosHeartbeatPauseOnBattery);
            SetBool(xml, XmlPrefix + "BatteryGlitchGuard", BatteryGlitchGuard);
            SetUInt(xml, XmlPrefix + "BatteryGlitchDropPercent", (uint) BatteryGlitchDropPercent);
            SetUInt(xml, XmlPrefix + "BatteryGlitchWindowMs",   (uint) BatteryGlitchWindowMs);
            SetUInt(xml, XmlPrefix + "BatteryGlitchHoldMs",     (uint) BatteryGlitchHoldMs);
            SetBool(xml, XmlPrefix + "BatteryGlitchGuardOnBattery", BatteryGlitchGuardOnBattery);
            SetBool(xml, XmlPrefix + "BatteryGlitchGuardDisableTimeout", BatteryGlitchGuardDisableTimeout);
            SetBool(xml, XmlPrefix + "BatteryGlitchGuardHoldAlways", BatteryGlitchGuardHoldAlways);
            SetBool(xml, XmlPrefix + "DisplayOffKeepAwake", DisplayOffKeepAwake);
            SetBool(xml, XmlPrefix + "FanDataPipeServer", FanDataPipeServer);
            SetBool(xml, XmlPrefix + "AcFlickerGuard", AcFlickerGuard);
            SetUInt(xml, XmlPrefix + "AcFlickerHoldMs", (uint) AcFlickerHoldMs);
            SetUInt(xml, XmlPrefix + "AcFlickerConfirmSamples", (uint) AcFlickerConfirmSamples);
            SetUInt(xml, XmlPrefix + "AcFlickerConfirmIntervalMs", (uint) AcFlickerConfirmIntervalMs);
            SetUInt(xml, XmlPrefix + "AcFlickerMaxDeferralMs", (uint) AcFlickerMaxDeferralMs);
            SetBool(xml, XmlPrefix + "AcFlickerPassivePoll", AcFlickerPassivePoll);
            SetBool(xml, XmlPrefix + "ThermalPanicEnabled", ThermalPanicEnabled);
            SetUInt(xml, XmlPrefix + "ThermalPanicTemperature", ThermalPanicTemperature);
            SetUInt(xml, XmlPrefix + "ThermalPanicHysteresis", ThermalPanicHysteresis);
            SetBool(xml, XmlPrefix + "TemperatureUseFahrenheit", TemperatureUseFahrenheit);

            // Save custom preset parameters directly inside OmenMon.xml
            SetString(xml, "//OmenMon/CustomPreset/Mode", CustomPresetFanMode);
            SetString(xml, "//OmenMon/CustomPreset/GpuPower", CustomPresetGpuPower);
            SetUInt(xml, "//OmenMon/CustomPreset/MaxRpm", (uint) CustomPresetMaxRpm);
            SetUInt(xml, "//OmenMon/CustomPreset/Fan0Level", CustomPresetCpuLevel);
            SetUInt(xml, "//OmenMon/CustomPreset/Fan1Level", CustomPresetGpuLevel);

            // Color presets
            MergeEntries(xml, (XmlElement) SetPath(xml, XmlPrefixColorPresets),
                XmlElementColorPreset, KeyOfColorPreset, ColorPreset, LoadedColorPresets,
                node => {
                    string name; BiosData.ColorTable table;
                    return TryParseColorPreset(node, out name, out table) ? BuildColorPreset(xml, name, table) : null; },
                (name, table) => BuildColorPreset(xml, name, table));

            // Continue with the configuration values
            SetUInt(xml, XmlPrefix + "EcFailLimit", (uint) EcFailLimit);
            SetUInt(xml, XmlPrefix + "EcMonInterval", (uint) EcMonInterval);
            SetUInt(xml, XmlPrefix + "EcMutexTimeout", (uint) EcMutexTimeout);
            SetUInt(xml, XmlPrefix + "EcRetryLimit", (uint) EcRetryLimit);
            SetUInt(xml, XmlPrefix + "EcWaitLimit", (uint) EcWaitLimit);
            SetUInt(xml, XmlPrefix + "EcWaitSpinCount", (uint) EcWaitSpinCount);
            SetUInt(xml, XmlPrefix + "EcWaitYieldCount", (uint) EcWaitYieldCount);
            SetBool(xml, XmlPrefix + "FanCountdownExtendAlways", FanCountdownExtendAlways);
            SetUInt(xml, XmlPrefix + "FanCountdownExtendInterval", (uint) FanCountdownExtendInterval);
            SetUInt(xml, XmlPrefix + "FanCountdownExtendThreshold", (uint) FanCountdownExtendThreshold);
            SetUInt(xml, XmlPrefix + "FanLevelMax", (uint) FanLevelMax);
            SetUInt(xml, XmlPrefix + "FanLevelMin", (uint) FanLevelMin);
            SetUInt(xml, XmlPrefix + "FanSafetyFloorCpu", FanSafetyFloorCpu);
            SetUInt(xml, XmlPrefix + "FanSafetyFloorGpu", FanSafetyFloorGpu);
            SetUInt(xml, XmlPrefix + "FanSafetyCoolTemperature", (uint) FanSafetyCoolTemperature);
            SetUInt(xml, XmlPrefix + "FanOffMaxMinutes", (uint) FanOffMaxMinutes);
            SetString(xml, XmlPrefix + "FanSafetyWarmCurve", FanSafetyWarmCurve);
            SetBool(xml, XmlPrefix + "FanLevelNeedManual", FanLevelNeedManual);
            SetBool(xml, XmlPrefix + "FanLevelUseEc", FanLevelUseEc);
            SetString(xml, XmlPrefix + "FanProgramDefault", FanProgramDefault);
            SetString(xml, XmlPrefix + "FanProgramDefaultAlt", FanProgramDefaultAlt);
            SetBool(xml, XmlPrefix + "FanProgramModeCheckFirst", FanProgramModeCheckFirst);
            SetBool(xml, XmlPrefix + "FanProgramSharedTemperature", FanProgramSharedTemperature);
            SetBool(xml, XmlPrefix + "FanProgramSuspend", FanProgramSuspend);

            // Fan programs
            MergeEntries(xml, (XmlElement) SetPath(xml, XmlPrefixFanPrograms),
                XmlElementFanProgram, KeyOfFanProgram, FanProgram, LoadedFanPrograms,
                node => {
                    FanProgramData program;
                    return TryParseFanProgram(node, out program) ? BuildFanProgram(xml, program.Name, program) : null; },
                (name, program) => BuildFanProgram(xml, name, program));

            // Continue with the configuration values
            SetString(xml, XmlPrefix + "GpuPowerDefault", GpuPowerDefault);
            SetUInt(xml, XmlPrefix + "GpuPowerSetInterval", (uint) GpuPowerSetInterval);
            SetBool(xml, XmlPrefix + "GuiCloseWindowExit", GuiCloseWindowExit);
            SetBool(xml, XmlPrefix + "GuiDpiChangeResize", GuiDpiChangeResize);
            SetBool(xml, XmlPrefix + "GuiDynamicIcon", GuiDynamicIcon);
            SetBool(xml, XmlPrefix + "GuiDynamicIconHasBackground", GuiDynamicIconHasBackground);
            SetBool(xml, XmlPrefix + "GuiStayOnTop", GuiStayOnTop);
            SetUInt(xml, XmlPrefix + "GuiSysInfoFontSize", (uint) GuiSysInfoFontSize);
            SetUInt(xml, XmlPrefix + "GuiTipDuration", (uint) GuiTipDuration);
            SetBool(xml, XmlPrefixKeyCustomAction + "Enabled", KeyCustomActionEnabled);
            SetString(xml, XmlPrefixKeyCustomAction + "ExecCmd", KeyCustomActionExecCmd);
            SetString(xml, XmlPrefixKeyCustomAction + "ExecArgs", KeyCustomActionExecArgs);
            SetBool(xml, XmlPrefixKeyCustomAction + "Minimized", KeyCustomActionMinimized);
            SetBool(xml, XmlPrefix + "KeyToggleColorPreset", KeyToggleColorPreset);
            SetBool(xml, XmlPrefix + "KeyToggleColorPresetSilent", KeyToggleColorPresetSilent);
            SetBool(xml, XmlPrefix + "KeyToggleFanProgram", KeyToggleFanProgram);
            SetBool(xml, XmlPrefix + "KeyToggleFanProgramCycleAll", KeyToggleFanProgramCycleAll);
            SetBool(xml, XmlPrefix + "KeyToggleFanProgramShowGuiFirst", KeyToggleFanProgramShowGuiFirst);
            SetBool(xml, XmlPrefix + "KeyToggleFanProgramSilent", KeyToggleFanProgramSilent);
            SetUInt(xml, XmlPrefix + "PresetRefreshRateHigh", (uint) PresetRefreshRateHigh);
            SetUInt(xml, XmlPrefix + "PresetRefreshRateLow", (uint) PresetRefreshRateLow);

            // Temperature sensors
            MergeEntries(xml, (XmlElement) SetPath(xml, XmlPrefixTemperature),
                XmlElementTemperatureSensor, KeyOfSensor, TemperatureSensor, LoadedSensors,
                node => {
                    string name; TemperatureSensorData sensor;
                    return TryParseSensor(node, out name, out sensor) ? BuildSensor(xml, name, sensor) : null; },
                (name, sensor) => BuildSensor(xml, name, sensor));

            // Model database entries
            MergeEntries(xml, (XmlElement) SetPath(xml, XmlPrefixModels),
                XmlElementModel, KeyOfModel, Models, LoadedModels,
                node => {
                    OmenMon.Hardware.Platform.PlatformPreset p;
                    return TryParseModel(node, out p) ? BuildModel(xml, p) : null; },
                (id, p) => BuildModel(xml, p));

            // The remaining configuration values
            SetUInt(xml, XmlPrefix + "UpdateIconInterval", (uint) UpdateIconInterval);
            SetUInt(xml, XmlPrefix + "UpdateMonitorInterval", (uint) UpdateMonitorInterval);
            SetUInt(xml, XmlPrefix + "UpdateProgramInterval", (uint) UpdateProgramInterval);

            // Write to a temporary file next to the configuration file
            string tempPath = FilePath + ".tmp";
            XmlWriterSettings xmlWriterSettings = new XmlWriterSettings();
            xmlWriterSettings.Encoding = new UTF8Encoding(XmlSaveBom);
            xmlWriterSettings.Indent = true;
            xmlWriterSettings.IndentChars = XmlSaveIndent;
            xmlWriterSettings.NewLineHandling = NewLineHandling.Replace;
            using(FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                using(XmlWriter xmlWriter = XmlWriter.Create(stream, xmlWriterSettings))
                    xml.Save(xmlWriter);
                stream.Flush(true);
            }

            // Swap it in
            try {
                if(File.Exists(FilePath))
                    File.Replace(tempPath, FilePath, null);
                else
                    File.Move(tempPath, FilePath);
            } catch {
                try { File.Delete(tempPath); } catch { }
                throw;
            }

        }

        // Merges one keyed collection into its parent element (see SaveCore())
        private static void MergeEntries<T>(
            XmlDocument xml, XmlElement parent, string elementName,
            Func<XmlElement, string> keyOf,
            IEnumerable<KeyValuePair<string, T>> entries,
            HashSet<string> loaded,
            Func<XmlElement, XmlElement> canonical,
            Func<string, T, XmlElement> build) {

            // Index the existing elements by key
            Dictionary<string, List<XmlElement>> existing =
                new Dictionary<string, List<XmlElement>>(loaded.Comparer);
            foreach(XmlNode child in parent.ChildNodes) {
                XmlElement element = child as XmlElement;
                if(element == null || element.Name != elementName)
                    continue;
                string key = keyOf(element);
                if(string.IsNullOrEmpty(key))
                    continue;
                List<XmlElement> list;
                if(!existing.TryGetValue(key, out list))
                    existing[key] = list = new List<XmlElement>();
                list.Add(element);
            }

            // Update changed entries and append new ones
            HashSet<string> present = new HashSet<string>(loaded.Comparer);
            foreach(KeyValuePair<string, T> entry in entries) {
                present.Add(entry.Key);
                XmlElement fresh = build(entry.Key, entry.Value);
                List<XmlElement> list;
                if(existing.TryGetValue(entry.Key, out list)) {

                    // The last element with a key is the one loading used
                    XmlElement current = list[list.Count - 1];
                    XmlElement parsed = canonical(current);
                    if(parsed == null || parsed.OuterXml != fresh.OuterXml)
                        parent.ReplaceChild(fresh, current);

                } else
                    parent.AppendChild(fresh);
            }

            // Remove only what was loaded and has since been deleted
            foreach(KeyValuePair<string, List<XmlElement>> pair in existing)
                if(!present.Contains(pair.Key) && loaded.Contains(pair.Key))
                    foreach(XmlElement element in pair.Value)
                        parent.RemoveChild(element);

            // The file now holds exactly what is in memory
            loaded.Clear();
            loaded.UnionWith(present);

        }

        // Collection element keys, matching the dictionary keys Load() uses
        private static string KeyOfColorPreset(XmlElement node) {
            return node.GetAttribute(XmlAttrColorPresetName);
        }

        private static string KeyOfFanProgram(XmlElement node) {
            return node.GetAttribute(XmlAttrFanProgramName);
        }

        private static string KeyOfSensor(XmlElement node) {
            return node.GetAttribute(XmlAttrTemperatureSensorSource) == XmlAttrTemperatureSensorSourceValueBios ?
                XmlAttrTemperatureSensorSourceValueBios : node.GetAttribute(XmlAttrTemperatureSensorName);
        }

        private static string KeyOfModel(XmlElement node) {
            return node.GetAttribute(XmlAttrModelProductId);
        }

        // Builds a color preset element
        private static XmlElement BuildColorPreset(XmlDocument xml, string name, BiosData.ColorTable table) {
            XmlElement node = xml.CreateElement(XmlElementColorPreset);

            // Store the preset name in an attribute
            node.SetAttribute(XmlAttrColorPresetName, name);

            // Store the preset parameter value as inner text
            node.InnerText = (Conv.GetColorString((int) table.Zone[(int) BiosData.KbdZone.Right].ValueReverse)
                + ":" + Conv.GetColorString((int) table.Zone[(int) BiosData.KbdZone.Middle].ValueReverse)
                + ":" + Conv.GetColorString((int) table.Zone[(int) BiosData.KbdZone.Left].ValueReverse)
                + ":" + Conv.GetColorString((int) table.Zone[(int) BiosData.KbdZone.Wasd].ValueReverse))
                    .ToUpper();
            return node;
        }

        // Builds a fan program element
        private static XmlElement BuildFanProgram(XmlDocument xml, string name, FanProgramData program) {
            XmlElement node = xml.CreateElement(XmlElementFanProgram);

            // Store the program name in an attribute
            node.SetAttribute(XmlAttrFanProgramName, name);

            // Create an element to store the fan mode
            node.AppendChild(xml.CreateElement(XmlElementFanProgramMode)).InnerText =
                Enum.GetName(typeof(BiosData.FanMode), program.FanMode);

            // Create an element to store the GPU power level
            node.AppendChild(xml.CreateElement(XmlElementFanProgramPower)).InnerText =
                Enum.GetName(typeof(BiosData.GpuPowerLevel), program.GpuPower);

            // For each programmed fan level
            foreach(byte temperature in program.Level.Keys) {

                // Create an element to store the level data
                XmlElement level = (XmlElement) node.AppendChild(xml.CreateElement(XmlElementFanProgramLevel));

                // Store the temperature
                level.SetAttribute(XmlAttrFanProgramLevelTemperature, Conv.GetString(temperature, 2, 10));

                // Store the CPU fan level
                level.AppendChild(xml.CreateElement(XmlElementFanProgramLevelCpu)).InnerText =
                    Conv.GetString(program.Level[temperature][0], 2, 10);

                // Store the GPU fan level
                level.AppendChild(xml.CreateElement(XmlElementFanProgramLevelGpu)).InnerText =
                    Conv.GetString(program.Level[temperature][1], 2, 10);

            }
            return node;
        }

        // Builds a temperature sensor element
        private static XmlElement BuildSensor(XmlDocument xml, string name, TemperatureSensorData sensor) {
            XmlElement node = xml.CreateElement(XmlElementTemperatureSensor);

            // Store the sensor name and source in attributes
            node.SetAttribute(XmlAttrTemperatureSensorName, name);
            node.SetAttribute(XmlAttrTemperatureSensorSource,
                sensor.Source == PlatformData.LinkType.EmbeddedController ?
                    XmlAttrTemperatureSensorSourceValueEc : XmlAttrTemperatureSensorSourceValueBios);

            if(!sensor.Use)
                node.SetAttribute(XmlAttrTemperatureSensorUse, XmlSaveBoolFalse);
            return node;
        }

        // Builds a model database element
        private static XmlElement BuildModel(XmlDocument xml, OmenMon.Hardware.Platform.PlatformPreset p) {
            XmlElement mnode = xml.CreateElement(XmlElementModel);
            mnode.SetAttribute(XmlAttrModelProductId, p.ProductId ?? "");
            mnode.SetAttribute(XmlAttrModelDisplayName, p.DisplayName ?? "");
            mnode.AppendChild(xml.CreateElement(XmlElementFanLevelReg0)).InnerText     = Conv.GetString((uint) p.FanLevelReg0, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanLevelReg1)).InnerText     = Conv.GetString((uint) p.FanLevelReg1, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanRateReadReg0)).InnerText  = Conv.GetString((uint) p.FanRateReadReg0, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanRateReadReg1)).InnerText  = Conv.GetString((uint) p.FanRateReadReg1, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanRateWriteReg0)).InnerText = Conv.GetString((uint) p.FanRateWriteReg0, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanRateWriteReg1)).InnerText = Conv.GetString((uint) p.FanRateWriteReg1, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanSpeedReg0)).InnerText     = Conv.GetString((uint) p.FanSpeedReg0, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementFanSpeedReg1)).InnerText     = Conv.GetString((uint) p.FanSpeedReg1, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementCountdownReg)).InnerText     = Conv.GetString((uint) p.CountdownReg, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementManualReg)).InnerText        = Conv.GetString((uint) p.ManualReg, 1, 10);
            // Only persist the override values when they differ from the legacy
            // FanManual.On/Off pair, so existing entries stay clean on round-trip.
            if(p.ManualValueOn != (byte) PlatformData.FanManual.On)
                mnode.AppendChild(xml.CreateElement(XmlElementManualValueOn)).InnerText = Conv.GetString((uint) p.ManualValueOn, 1, 10);
            if(p.ManualValueOff != (byte) PlatformData.FanManual.Off)
                mnode.AppendChild(xml.CreateElement(XmlElementManualValueOff)).InnerText = Conv.GetString((uint) p.ManualValueOff, 1, 10);
            if(p.TempCpuReg != 0)
                mnode.AppendChild(xml.CreateElement(XmlElementTempCpuReg)).InnerText = Conv.GetString((uint) p.TempCpuReg, 1, 10);
            if(p.TempGpuReg != 0)
                mnode.AppendChild(xml.CreateElement(XmlElementTempGpuReg)).InnerText = Conv.GetString((uint) p.TempGpuReg, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementModeReg)).InnerText          = Conv.GetString((uint) p.ModeReg, 1, 10);
            mnode.AppendChild(xml.CreateElement(XmlElementSwitchReg)).InnerText        = Conv.GetString((uint) p.SwitchReg, 1, 10);
            return mnode;
        }

        // Adds or updates a model preset in the dictionary and persists to XML
        public static void SaveModel(OmenMon.Hardware.Platform.PlatformPreset preset) {
            Models[preset.ProductId] = preset;
            Save();
        }

        // Sets a Boolean flag in the XML configuration file
        private static bool SetBool(XmlDocument xml, string node, bool value) {
            try {
                (SetPath(xml, node)).InnerText = value ? XmlSaveBoolTrue : XmlSaveBoolFalse;
                return true;
            } catch {  }
            return false;
        }

        // Ensures all intermediate nodes exist along an XML search path,
        // then returns the requested node as an object
        private static XmlNode SetPath(XmlDocument xml, XmlNode parent, string path) {
            XmlNode node;

            // Split the path into individual node names
            string[] nodes = path.Trim('/').Split('/');

            // If the next node name is empty, return the parent node
            if(string.IsNullOrEmpty(nodes[0]))
                return parent;

            // Create the node if it does not exist
            if((node = parent.SelectSingleNode(nodes[0])) == null)
                node = parent.AppendChild(xml.CreateElement(nodes[0]));

            // Recursively process the remaining nodes along the path
            return SetPath(xml, node,
                path.Length > nodes[0].Length ?
                    path.Substring(nodes[0].Length + 1) : "");

        }

        // Wrapper for SetPath() starting at document root
        private static XmlNode SetPath(XmlDocument xml, string path) {
            return SetPath(xml, (XmlNode) xml, path);
        }

        // Sets a string value in the XML configuration file
        private static bool SetString(XmlDocument xml, string node, string value) {
            try {
                (SetPath(xml, node)).InnerText = value;
                return true;
            } catch {  }
            return false;
        }

        // Sets an unsigned double word-sized value in the XML configuration file
        private static bool SetUInt(XmlDocument xml, string node, uint value, int padding = 1, int nbase = 10) {
            try {
                (SetPath(xml, node)).InnerText = Conv.GetString(value, padding, nbase);
                return true;
            } catch {  }
            return false;
        }
#endregion

#region Error Handling
        // Retrieves a concatenated error message
        public static string GetError(string messageIds, Exception e = null) {

            // A bit of a chicken and egg problem
            if(Config.Locale == null)
                return "Failed to instantiate the localizable message system";

            int messageCount = 0;
            string message = "";

            // Obtain a localization string for each message identifier
            foreach(string messageId in messageIds.Split('|')) {

                // If multiple messages, add a separator
                // between the first and the second only
                message += messageCount > 0 ? messageCount > 1 ? "" : ": " : "";

                // Append the next message
                // Exception message is a special case
                if(messageId == "EXCEPTION")
                    message += e != null ? e.Message : Config.Locale.Get("ErrUnexpectedReally");
                else
                    message += Config.Locale.Get(messageId);

                // Count the messages processed so far
                messageCount++;
            }

            return message;
        }
#endregion

    }

}

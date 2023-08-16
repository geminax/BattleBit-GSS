using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace GSService
{
    public partial class Service : ServiceBase
    {
        private EventLog eventLog;
        private int gsInterval;
        private int cachedChangeToken;
        private string cacheFilePath;
        private string apiEndpoint;
        private string steamUsername;
        private string serverName;
        private string serverPassword;
        public Service()
        {
            InitializeComponent();
            eventLog = new EventLog();
            if (!EventLog.SourceExists("GSS"))
            {
                EventLog.CreateEventSource(
                    "GSS", "GSSLog");
            }
            eventLog.Source = "GSS";
            eventLog.Log = "GSSLog";

            gsInterval = int.Parse(ConfigurationManager.AppSettings["gss_interval"]);
            cacheFilePath = ConfigurationManager.AppSettings["battlebit_dir"] + ConfigurationManager.AppSettings["gss_cache_file"];
        }

        protected override void OnStart(string[] args)
        {
            SendMessage("-------- Starting GSS --------", "Info");

            RetrieveEnvVar("gss_api_endpoint", out apiEndpoint, true);
            if (apiEndpoint == null)
                return;

            RetrieveEnvVar("steam_username", out steamUsername, true);
            if (steamUsername == null)
                return;

            RetrieveEnvVar("gss_server_name", out serverName, true);
            if (serverName == null)
                return;

            RetrieveEnvVar("gss_server_password", out serverPassword, true);
            if (serverPassword == null)
                return;

            Task.Run(() => Start());
        }

        private void RetrieveEnvVar(string key, out string member, bool stopIfNull)
        {
            string envVarName = ConfigurationManager.AppSettings[key];
            member = Environment.GetEnvironmentVariable(envVarName);
            if (member == null)
            {
                if (stopIfNull)
                {
                    SendMessage($"{envVarName} is not set", "Fatal");
                    Stop();
                }
                else
                {
                    SendMessage($"{envVarName} is not set", "Error");
                } 
            }
        }

        protected override void OnStop()
        {
            KillGameServer();
            SendMessage("-------- Stopping GSS --------", "Info");
        }

        private void Start()
        {
            // start from scratch if cache not available
            if (MissingGSSCache())
            {
                SendMessage("Missing GSS Cache. Starting from scratch.", "Info");
                StartFromScratch();
            }

            cachedChangeToken = CachedChangeToken();
            if (cachedChangeToken == -1)
            {
                SendMessage("Unable to retrieve cached change token", "Fatal");
                Stop();
            }

            // kill server if running
            if (GameServerRunning())
                KillGameServer();

            ReadServerArgsConfig(out var serverArgs);

            serverArgs.AddRange(new string[]
            {
                "-batchmode",
                "-nographics",
                "-Name=" + serverName,
                "-Password=" + serverPassword,
                "-apiEndpoint=" + apiEndpoint
            });

            bool MarkForReinstall = false;
            while (true)
            {
                if (MarkForReinstall)
                {
                    StartFromScratch();
                    MarkForReinstall = false;
                }
            
                if (UpdateAvailable())
                {
                    SendMessage("Update Available.", "Debug");
                    if (!UpdateGameServer())
                    {
                        SendMessage("Failed to update Game Server.", "Fatal");
                        Stop();
                    } 
                    else
                    {
                        SendMessage("Game Server updated", "Info");
                    }
                }

                if (!GameServerRunning())
                {
                    SendMessage("Game Server is not running", "Debug");
                    if (!StartGameServer(serverArgs))
                    {
                        SendMessage("Failed to start Game Server. Marking for Reinstall.", "Error");
                        MarkForReinstall = true;
                    }
                    else
                    {
                        SendMessage("Game Server started", "Info");
                    }
                }       
                
                Thread.Sleep(gsInterval);
            }
        }

        private bool StartGameServer(List<string> args)
        {
            string argsStr = string.Join(" ", args);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = @"C:\battlebit\" + ConfigurationManager.AppSettings["battlebit_exe"],
                Arguments = argsStr, 
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            SendMessage($"Trying to start Game Server {startInfo.FileName} in {startInfo.WorkingDirectory} with args {argsStr}", "Debug");


            Process process = new Process
            {
                StartInfo = startInfo,
            };
            process.OutputDataReceived += (sender, e) => { SendMessage(e.Data, "GS_OUT"); };
            process.ErrorDataReceived += (sender, e) => { SendMessage(e.Data, "GS_ERROR"); };

            try
            {
                return process.Start();  
            }
            catch (Exception ex)
            {
                SendMessage($"An error occurred: {ex.Message}", "Error");
                return false;
            }
        }

        private bool GameServerRunning()
        {
            Process[] processes = Process.GetProcessesByName("BattleBit");
            if (processes.Length > 0)
                return true;
            return false;
        }

        private void KillGameServer()
        {
            SendMessage("Trying to kill Game Server", "Debug");
            int killcount = 0;
            try
            {
                Process[] processes = Process.GetProcessesByName("BattleBit"); 
                foreach (Process p in processes)
                {
                    p.Kill();
                    killcount++;
                }
            }
            catch (InvalidOperationException)
            {
                // no game server running
            }
            catch (Exception ex)
            {
                SendMessage($"Process could not be terminated: {ex.Message}", "Error");
            }
            if (killcount == 0)
                SendMessage("Nothing was killed", "Debug");
            else
                SendMessage($"Killed Game Server {killcount} times", "Debug");
        }

        private int AvailableChangeToken()
        {
            string app_id = ConfigurationManager.AppSettings["battlebit_app_id"];
            using (HttpClient httpClient = new HttpClient())
            {
                string apiUrl = ConfigurationManager.AppSettings["steamcmd_app_api"] + app_id;
                HttpResponseMessage response = httpClient.GetAsync(apiUrl).Result;

                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = response.Content.ReadAsStringAsync().Result;
                    JObject jsonObj = JObject.Parse(jsonResponse);
                    return jsonObj["data"][app_id]["_change_number"].Value<int>();
                }
                else
                {
                    SendMessage($"API request failed: {response.StatusCode}", "Error");
                }
            }
            return -1;
        }

        private bool UpdateAvailable()
        {
            int availableChangeToken = AvailableChangeToken();
            if (availableChangeToken == -1)
            {
                SendMessage("Unable to retrieve available change token.", "Error");
            }
            else
            {
                if (availableChangeToken != cachedChangeToken)
                {
                    return true;
                }
            }
            return false;
        }

        private bool UpdateGameServer()
        {
            SendMessage("Updating Game Server", "Debug");
            if (GameServerRunning())
                KillGameServer();

            string[] steamcmd_args =
            {
                $"+force_install_dir {ConfigurationManager.AppSettings["battlebit_dir"]}",
                $"+login {steamUsername}",
                $"+app_update {ConfigurationManager.AppSettings["battlebit_app_id"]}",
                "+exit"
            };
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ConfigurationManager.AppSettings["steamcmd_file_path"],
                Arguments = string.Join(" ", steamcmd_args),
            };

            Process process = new Process
            {
                StartInfo = startInfo
            };

            try
            {
                if (!process.Start())
                    return false;

                process.WaitForExit();
            }
            catch (Exception ex)
            {
                SendMessage($"An error occurred: {ex.Message}", "Error");
                return false;
            }

            return true;

        }

        private bool CreateGSSCache()
        {
            SendMessage("Creating GSS Cache", "Debug");
            try
            {
                if (!File.Exists(cacheFilePath))
                {
                    cachedChangeToken = AvailableChangeToken();
                    File.WriteAllText(cacheFilePath, cachedChangeToken.ToString());
                }
                else
                {
                    // not really an error for the file to exist, but we treat it as one
                    return false;
                }
            }
            catch (Exception ex)
            {
                SendMessage($"Failed to create GSS cache: {ex.Message}", "Error");
                return false;
            }
            return true;
        }

        private int CachedChangeToken()
        {
            try
            {
                if (File.Exists(cacheFilePath))
                {
                    return int.Parse(File.ReadAllText(cacheFilePath));
                }
            }
            catch (Exception ex)
            {
                SendMessage($"Failed to retrieve change token: {ex.Message}", "Error");
            }
            return -1;
        }

        private bool MissingGSSCache()
        {
            if (File.Exists(cacheFilePath))
                return false;
            return true;
        }

        private bool ReinstallGameServer()
        {
            SendMessage("Reinstalling Game Server", "Debug");
            if (GameServerRunning())
                KillGameServer();

            string bbDirStr = ConfigurationManager.AppSettings["battlebit_dir"];
            try
            {
                if (Directory.Exists(bbDirStr))
                {
                    Directory.Delete(bbDirStr, true);
                }

                Directory.CreateDirectory(bbDirStr);

            }
            catch (Exception ex)
            {
                SendMessage($"Failed to recreate battlebit directory: {ex.Message}", "Error");
                return false;
            }

            return UpdateGameServer();
        }

        private void StartFromScratch()
        {
            SendMessage("Starting from Scratch", "Debug");
            if (!ReinstallGameServer())
            {
                SendMessage("Failed to re/install game server", "Fatal");
                Stop();
            }
            if (!CreateGSSCache())
            {
                SendMessage("Failed to create GSS cache", "Fatal");
                Stop();
            }
        }

        private void SendMessage(string message, string level)
        {
            string str = "";
            ASCIIEncoding aSCIIEncoding = new ASCIIEncoding();
            object[] objArray = new object[] { "gs_srv", level, message, Environment.MachineName.ToLower().Trim() };
            str = string.Format("{1} {0} ({3}) - {2}", objArray);
            byte[] bytes = aSCIIEncoding.GetBytes(str);

            eventLog.WriteEntry(str);

            string syslog = Environment.GetEnvironmentVariable(ConfigurationManager.AppSettings["gss_syslog_name"]);
            if (syslog == null)
                return;
            UdpClient udpClient = new UdpClient(syslog, 514);
            udpClient.Send(bytes, (int)bytes.Length);
            udpClient.Close();
        }

        private void ReadServerArgsConfig(out List<string> serverargs)
        {
            SendMessage("Reading ServerArgs", "Info");

            string executablePath = Process.GetCurrentProcess().MainModule.FileName;
            string executableDirectory = System.IO.Path.GetDirectoryName(executablePath);
            serverargs = new List<string>();
            try
            {
                var jStr = File.ReadAllText($"{executableDirectory}\\ServerArgs.json");
                JObject jObj = JObject.Parse(jStr);

                foreach (var property in jObj)
                {
                    var option = $"-{property.Key}={property.Value}";
                    SendMessage($"Adding option: {option}", "Debug");
                    serverargs.Add($"-{property.Key}={property.Value}");
                }
            }
            catch (Exception ex)
            {
                SendMessage($"Exception occurred while reading server args config: {ex.Message}", "Error");
                Stop();
            }
        }
    }
}

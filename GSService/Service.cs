using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Hosting;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Net.Http;
using Newtonsoft.Json;
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
        public Service()
        {
            InitializeComponent();
            eventLog = new System.Diagnostics.EventLog();
            if (!System.Diagnostics.EventLog.SourceExists("GSS"))
            {
                System.Diagnostics.EventLog.CreateEventSource(
                    "GSS", "GSSLog");
            }
            eventLog.Source = "GSS";
            eventLog.Log = "GSSLog";

            gsInterval = int.Parse(ConfigurationManager.AppSettings["gss_interval"]);
            cacheFilePath = ConfigurationManager.AppSettings["battlebit_dir"] + ConfigurationManager.AppSettings["gss_cache_file"];
        }

        protected override void OnStart(string[] args)
        {
            sendMessage("-------- Starting GSS --------", "Info");

            apiEndpoint = Environment.GetEnvironmentVariable(ConfigurationManager.AppSettings["gss_api_endpoint"]);
            if (apiEndpoint == null)
            {
                sendMessage("GSS_API_ENDPOINT env var not set", "Error");
                Stop();
            }

            steamUsername = Environment.GetEnvironmentVariable(ConfigurationManager.AppSettings["steam_username"]);
            if (steamUsername == null)
            {
                sendMessage("STEAM_USERNAME env var not set", "Error");
                Stop();
            }

            Task.Run(() => Start());
        }

        protected override void OnStop()
        {
            sendMessage("-------- Stopping GSS --------", "Info");
        }

        private void Start()
        {
            // start from scratch if cache not available
            if (MissingGSSCache())
            {
                sendMessage("Missing GSS Cache. Starting from scratch.", "Info");
                StartFromScratch();
            }

            cachedChangeToken = CachedChangeToken();
            if (cachedChangeToken == -1)
            {
                sendMessage("Unable to retrieve cached change token", "Error");
            }

            // kill server if running
            if (GameServerRunning())
                KillGameServer();

            string[] serverArgs = {
                "-batchmode",
                "-nographics",
                "-LocalIp=0.0.0.0",
                "-Port=29595",
                "-Hz=144",
                "-FirstMap=Construction",
                "-FirstGamemode=DOMI",
                "-FixedSize=medium",
                "-apiEndpoint=" + apiEndpoint
            };

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
                    if (!UpdateGameServer())
                    {
                        sendMessage("Failed to update Game Server.", "Error");
                        Stop();
                    }
                }

                if (!GameServerRunning())
                {
                    if (!StartGameServer(serverArgs))
                    {
                        sendMessage("Failed to start Game Server. Marking for Reinstall.", "Error");
                        MarkForReinstall = true;
                    }
                }       
                
                Thread.Sleep(gsInterval);
            }
        }

        private bool StartGameServer(string[] args)
        {
            string argsStr = string.Join(" ", args);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ConfigurationManager.AppSettings["battlebit_exe"],
                Arguments = argsStr, 
                WorkingDirectory = @"C:\battlebit" // Optional working directory
            };

            Process process = new Process
            {
                StartInfo = startInfo
            };

            try
            {
                return process.Start();  
            }
            catch (Exception ex)
            {
                sendMessage($"An error occurred: {ex.Message}", "Error");
                return false;
            }
        }

        private bool GameServerRunning()
        {
            Process[] processes = Process.GetProcessesByName(ConfigurationManager.AppSettings["battlebit_exe"]);
            if (processes.Length > 0)
                return true;
            return false;
        }

        private void KillGameServer()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(ConfigurationManager.AppSettings["battlebit_exe"]); 
                foreach (Process p in processes)
                {
                    p.Kill();
                }
            }
            catch (InvalidOperationException ex)
            {
                // no game server running
            }
            catch (Exception ex)
            {
                sendMessage($"Process could not be terminated: {ex.Message}", "Error");
            }
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
                    sendMessage($"API request failed: {response.StatusCode}", "Error");
                }
            }
            return -1;
        }

        private bool UpdateAvailable()
        {
            int availableChangeToken = AvailableChangeToken();
            if (availableChangeToken == -1)
            {
                sendMessage("Unable to retrieve available change token.", "Error");
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
                sendMessage($"An error occurred: {ex.Message}", "Error");
                return false;
            }

            return true;

        }

        private bool CreateGSSCache()
        {
            try
            {
                if (!File.Exists(cacheFilePath))
                {
                    File.WriteAllText(cacheFilePath, AvailableChangeToken().ToString());
                }
                else
                {
                    // not really an error for the file to exist, but we treat it as one
                    return false;
                }
            }
            catch (Exception ex)
            {
                sendMessage($"Failed to create GSS cache: {ex.Message}", "Error");
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
                sendMessage($"Failed to retrieve change token: {ex.Message}", "Error");
            }
            return -1;
        }

        private bool MissingGSSCache()
        {
            if (File.Exists(cacheFilePath))
                return true;
            return false;
        }

        private bool ReinstallGameServer()
        {
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
                sendMessage($"Failed to recreate battlebit directory: {ex.Message}", "Error");
                return false;
            }

            return UpdateGameServer();
        }

        private void StartFromScratch()
        {
                if (!ReinstallGameServer())
                {
                    sendMessage("Failed to re/install game server", "Error");
                    Stop();
                }
                if (!CreateGSSCache())
                {
                    sendMessage("Failed to create GSS cache", "Error");
                    Stop();
                }
        }

        private void sendMessage(string message, string level)
        {
            string str = "";
            ASCIIEncoding aSCIIEncoding = new ASCIIEncoding();
            object[] objArray = new object[] { "gs_srv", level, message, Environment.MachineName.ToLower().Trim() };
            str = string.Format("({3}): {0} {1} {2}", objArray);
            byte[] bytes = aSCIIEncoding.GetBytes(str);

            eventLog.WriteEntry(str);

            string syslog = Environment.GetEnvironmentVariable(ConfigurationManager.AppSettings["gss_syslog_name"]);
            if (syslog == null)
                return;
            UdpClient udpClient = new UdpClient(syslog, 514);
            udpClient.Send(bytes, (int)bytes.Length);
            udpClient.Close();
        }
    }
}

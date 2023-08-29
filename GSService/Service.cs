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
using System.Security.Cryptography;

namespace GSService
{
    public partial class Service : ServiceBase
    {
        private EventLog eventLog;
        private int gsInterval;
        private string installedHash;
        private string battlebit_dir;
        private string battlebit_temp_dir;

        private string apiEndpoint;
        private string apiToken;
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
            battlebit_dir = ConfigurationManager.AppSettings["battlebit_dir"];
            battlebit_temp_dir = ConfigurationManager.AppSettings["battlebit_temp_dir"];
        }

        protected override void OnStart(string[] args)
        {
            SendMessage("-------- Starting GSS --------", "Info");

            RetrieveEnvVar("gss_api_endpoint", out apiEndpoint, true);
            if (apiEndpoint == null)
                return;

            RetrieveEnvVar("gss_api_token", out apiToken, true);
            if (apiToken == null)
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
            bool MarkForReinstall = false;

            if (!UpdateGameServer(ConfigurationManager.AppSettings["battlebit_dir"], true))
            {
                SendMessage("Failed to update Game Server", "Fatal");
                Stop();
            }

            installedHash = CalculateBinaryFilesHash(ConfigurationManager.AppSettings["battlebit_dir"], new SHA256Managed());
            SendMessage($"Installed Hash: {installedHash}", "Debug");

            // kill server if running
            if (GameServerRunning())
                KillGameServer();

            ReadServerArgsConfig(out var serverArgs);

            serverArgs.AddRange(new string[]
            {
                "-batchmode",
                "-nographics",
                $"-Name={serverName}",
                //$"-Password={serverPassword}",
                $"-apiEndpoint={apiEndpoint}",
                $"-apiToken={apiToken}"
            });

            while (true)
            {
                if (MarkForReinstall)
                {
                    StartFromScratch();
                    MarkForReinstall = false;
                }
            
                if (UpdateAvailable())
                {
                    SendMessage("Update Available", "Debug");
                    if (!UpdateGameServer(ConfigurationManager.AppSettings["battlebit_dir"], true))
                    {
                        SendMessage("Failed to update Game Server", "Fatal");
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
                        SendMessage("Failed to start Game Server; Marking for Reinstall", "Error");
                        MarkForReinstall = true;
                    }
                    else
                    {
                        SendMessage("Game Server started", "Info");
                    }
                }

                if (MarkForReinstall)
                    Thread.Sleep(20000);
                else
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
                if (process.Start())
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    return true;
                } 
            }
            catch (Exception ex)
            {
                SendMessage($"An error occurred: {ex.Message}", "Error");
                return false;
            }
            return false;
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
        private bool IsBinaryFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            return extension == ".exe" || extension == ".dll";
        }

        private string CalculateBinaryFilesHash(string directoryPath, HashAlgorithm algorithm)
        {
            StringBuilder hashBuilder = new StringBuilder();

            foreach (string filePath in Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories))
            {
                if (IsBinaryFile(filePath))
                {
                    using (FileStream fileStream = File.OpenRead(filePath))
                    {
                        byte[] hashBytes = algorithm.ComputeHash(fileStream);
                        string hashString = BitConverter.ToString(hashBytes).Replace("-", "");
                        hashBuilder.Append(hashString);
                    }
                }
            }

            return hashBuilder.ToString();
        }

        private bool UpdateAvailable()
        {
            try
            {
                if (Directory.Exists(ConfigurationManager.AppSettings["battlebit_temp_dir"]))
                {
                    Directory.Delete(ConfigurationManager.AppSettings["battlebit_temp_dir"], true);
                }

                Directory.CreateDirectory(ConfigurationManager.AppSettings["battlebit_temp_dir"]);

            }
            catch (Exception ex)
            {
                SendMessage($"Failed to recreate battlebit directory: {ex.Message}", "Error");
                return false;
            }

            if (!UpdateGameServer(ConfigurationManager.AppSettings["battlebit_temp_dir"], false))
                return false;

            var hash = CalculateBinaryFilesHash(ConfigurationManager.AppSettings["battlebit_temp_dir"], new SHA256Managed());

            SendMessage($"Comparing Hashes, installed: {installedHash} , available: {hash}", "Debug");

            if (installedHash != hash)
                return true;
            return false;
        }

        private bool UpdateGameServer(string install_dir, bool killrunning)
        {
            SendMessage($"Updating Game Server at location {install_dir}", "Debug");
            if (killrunning)
            {
                if (GameServerRunning())
                    KillGameServer();
            }

            List<string> steamcmd_args = new List<string>
            {
                $"+force_install_dir {install_dir}",
                $"+login {steamUsername}",
                $"+app_update {ConfigurationManager.AppSettings["battlebit_app_id"]}",
            };

            // Remove when moved to production
            steamcmd_args.Add($"-beta community-testing");

            steamcmd_args.Add("+exit");

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
                {
                    SendMessage("No new steamcmd process could be started for updating", "Error");
                    return false;
                }

                process.WaitForExit();
            }
            catch (Exception ex)
            {
                SendMessage($"An error occurred: {ex.Message}", "Error");
                return false;
            }

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

            return UpdateGameServer(bbDirStr, true);
        }

        private void StartFromScratch()
        {
            SendMessage("Starting from Scratch", "Debug");
            if (!ReinstallGameServer())
            {
                SendMessage("Failed to re/install game server", "Fatal");
                Stop();
            }
            installedHash = CalculateBinaryFilesHash(ConfigurationManager.AppSettings["battlebit_dir"], new SHA256Managed());
            SendMessage($"Installed Hash: {installedHash}", "Debug");
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

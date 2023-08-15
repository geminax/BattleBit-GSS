using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace GSServiceTask
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            try
            {
                if (Environment.HasShutdownStarted)
                {
                    Program.SendMessage("Windows is shutting down. GS Service Task will not run...", "Debug");
                }
                else
                {
                    ServiceController serviceController = new("GSService");
                    if (serviceController.Status == ServiceControllerStatus.Stopped)
                    {
                        serviceController.Start();
                        serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        Program.SendMessage("GSS stopped. Windows task restarted the service", "Alert");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.SendMessage($"Exception thrown while task attempted to restart GSService: ${ex.Message}", "Error");
            }
        }

        private static void SendMessage(string message, string level)
        {
            string? syslog = Environment.GetEnvironmentVariable("GSS_SYSLOG_SERVER_NAME");
            if (syslog == null)
                return;
            string str = "";
            ASCIIEncoding aSCIIEncoding = new();
            object[] objArray = new object[] { "gs_srv", level, message, Environment.MachineName.ToLower().Trim() };
            str = string.Format("{0} - ({3}): {1} {2}", objArray);
            byte[] bytes = aSCIIEncoding.GetBytes(str.Replace(">", "(gt)"));
            UdpClient udpClient = new(syslog, 514);
            udpClient.Send(bytes, (int)bytes.Length);
            udpClient.Close();
        }
    }
}
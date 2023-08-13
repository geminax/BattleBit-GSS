using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;

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
                    Program.sendMessage("Windows is shutting down. GS Service Task will not run...", "Debug");
                }
                else
                {
                    ServiceController serviceController = new("GSService");
                    if (serviceController.Status == ServiceControllerStatus.Stopped)
                    {
                        serviceController.Start();
                        serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        Program.sendMessage("Paperspace service stopped. Windows task restarted the service", "Alert");
                    }
                }
            }
            catch (Exception)
            {
                Program.sendMessage("Exception thrown while task attempted to restart GSService", "Error");
            }
        }

        private static void sendMessage(string message, string level)
        {
            string syslog = Environment.GetEnvironmentVariable("SYSLOG_SERVER_NAME");
            if (syslog == null)
                return;
            string str = "";
            ASCIIEncoding aSCIIEncoding = new ASCIIEncoding();
            object[] objArray = new object[] { "gs_srv", level, message, Environment.MachineName.ToLower().Trim() };
            str = string.Format("({3}): {0} {1} {2}", objArray);
            byte[] bytes = aSCIIEncoding.GetBytes(str.Replace(">", "(gt)"));
            UdpClient udpClient = new UdpClient(syslog, 514);
            udpClient.Send(bytes, (int)bytes.Length);
            udpClient.Close();
        }
    }
}
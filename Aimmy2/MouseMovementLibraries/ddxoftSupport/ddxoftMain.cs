using Other;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;

namespace MouseMovementLibraries.ddxoftSupport
{
    internal class DdxoftMain
    {
        public static ddxoftMouse ddxoftInstance = new();
        private static readonly string ddxoftpath = "ddxoft.dll";
        private static readonly string ddxoftUri = "https://gitlab.com/marsqq/extra-files/-/raw/main/ddxoft.dll";

        private static async Task DownloadDdxoft()
        {
            try
            {
                LogManager.Log(LogManager.LogLevel.Info, $"{ddxoftpath} is missing, attempting to download {ddxoftpath}.", true);

                using HttpClient httpClient = new();

                var response = await httpClient.GetAsync(new Uri(ddxoftUri));
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(ddxoftpath, content);
                    LogManager.Log(LogManager.LogLevel.Info, $"{ddxoftpath}has downloaded successfully, please re-select ddxoft Virtual Input Driver to load the DLL.", true);
                }
            }
            catch
            {
                LogManager.Log(LogManager.LogLevel.Error, $"{ddxoftpath} has failed to download, please try a different Mouse Movement Method.", true);
            }
        }

        public static async Task<bool> DLLLoading()
        {
            try
            {
                if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) == false)
                {
                    MessageBox.Show("The ddxoft Virtual Input Driver requires Aimmy to be run as an administrator, please close Aimmy and run it as administrator to use this movement method.", "Aimmy");
                    return false;
                }

                if (!File.Exists(ddxoftpath))
                {
                    await DownloadDdxoft();
                    return false;
                }

                if (ddxoftInstance.Load(ddxoftpath) != 1 || ddxoftInstance.btn!(0) != 1)
                {
                    MessageBox.Show("The ddxoft virtual input driver is not compatible with your PC, please try a different Mouse Movement Method.", "Aimmy");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load ddxoft virtual input driver.\n\n" + ex.ToString(), "Aimmy");
                return false;
            }
        }

        public static async Task<bool> Load() => await DLLLoading();
    }
}
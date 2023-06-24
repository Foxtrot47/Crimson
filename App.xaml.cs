using Microsoft.UI.Xaml;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.Core;
using WinUiApp.Core;
using Windows.UI.Core;

namespace WinUiApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();
            m_window.Closed += OnExit;
        }

        // Save gamedata to storage on application exit
        private static async void OnExit(object sender, object e)
        {
            await StateManager.UpdateJsonFileAsync();
        }

        private Window m_window;
    }
}

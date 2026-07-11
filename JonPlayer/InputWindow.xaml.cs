using System.Windows;

namespace JonPlayer
{
    public partial class InputWindow : Window
    {
        public string InputUrl { get; private set; } = string.Empty;

        public InputWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Proper focus after window is loaded (Focus() in ctor doesn't work reliably)
            TxtUrl.Focus();
            TxtUrl.SelectAll(); // ready for paste / replace
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtUrl.Text))
            {
                InputUrl = TxtUrl.Text.Trim();
                DialogResult = true;
                Close();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

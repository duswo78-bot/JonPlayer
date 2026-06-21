using System.Windows;

namespace JonPlayer
{
    public partial class InputWindow : Window
    {
        public string InputUrl { get; private set; } = string.Empty;

        public InputWindow()
        {
            InitializeComponent();
            TxtUrl.Focus();
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

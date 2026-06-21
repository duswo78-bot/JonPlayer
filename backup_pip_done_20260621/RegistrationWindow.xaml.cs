using System;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace JonPlayer
{
    public partial class RegistrationWindow : Window
    {
        public bool IsRegisteredEmployee { get; private set; } = false;

        public RegistrationWindow()
        {
            InitializeComponent();
        }

        private void ChkIsAptiv_Checked(object sender, RoutedEventArgs e)
        {
            PanelInput.Visibility = Visibility.Visible;
            BtnRegister.Visibility = Visibility.Visible;
            TxtSkip.Visibility = Visibility.Collapsed;
            UpdateRegisterButtonState();
        }

        private void ChkIsAptiv_Unchecked(object sender, RoutedEventArgs e)
        {
            PanelInput.Visibility = Visibility.Collapsed;
            BtnRegister.Visibility = Visibility.Collapsed;
            TxtSkip.Visibility = Visibility.Visible;
        }

        private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateRegisterButtonState();
        }

        private void TxtEmail_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (TxtEmail.Text == "@aptiv.com")
            {
                TxtEmail.CaretIndex = 0;
            }
        }

        private void UpdateRegisterButtonState()
        {
            if (BtnRegister != null)
            {
                BtnRegister.IsEnabled = !string.IsNullOrWhiteSpace(TxtName?.Text) && !string.IsNullOrWhiteSpace(TxtEmail?.Text);
            }
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            SaveRegistrationStatus(false);
            this.Close();
        }

        private void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            if (ChkIsAptiv.IsChecked == true)
            {
                string name = TxtName.Text.Trim();
                string email = TxtEmail.Text.Trim();

                if (string.IsNullOrEmpty(name))
                {
                    System.Windows.MessageBox.Show("Please enter your name. (이름을 입력해 주세요.)", "Required Field", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(email))
                {
                    System.Windows.MessageBox.Show("Please enter your email. (이메일을 입력해 주세요.)", "Required Field", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool isAptiv = true; // User checked the box, so they are registering as an employee

                string to = "yeon.jae.park@aptiv.com";
                string subject = "JonPlayer Registration";
                string dateStr = DateTime.Now.ToString("yyyy-MM-dd");

                string body = $@"Name: {name}
Email: {email}
Date: {dateStr}

본인은 APTIV 직원으로서 JonPlayer 사용 등록을 합니다.
I hereby register for the use of JonPlayer as an APTIV employee.

감사합니다.
Thank you.


-- Please send any bug reports or improvement suggestions encountered while using JonPlayer to the email below:  yeon.jae.park@aptiv.com";

                try
                {
                    string mailto = $"mailto:{to}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
                    var psi = new ProcessStartInfo
                    {
                        FileName = mailto,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to open email client: {ex.Message}\n\nPlease send an email to {to} manually.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                SaveRegistrationStatus(isAptiv);
                this.Close();
            }
        }

        private void SaveRegistrationStatus(bool isEmployee)
        {
            IsRegisteredEmployee = isEmployee;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\JonPlayer"))
                {
                    key.SetValue("HasLaunchedBefore", 1, RegistryValueKind.DWord);
                    key.SetValue("IsAptivEmployee", isEmployee ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save registry: " + ex.Message);
            }
        }
    }
}

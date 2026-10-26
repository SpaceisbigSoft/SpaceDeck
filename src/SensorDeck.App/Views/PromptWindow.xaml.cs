using System.Windows;

namespace SensorDeck.App.Views;

/// <summary>A one-line text prompt (dashboard names).</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
        Title = title;
        LabelText.Text = label;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

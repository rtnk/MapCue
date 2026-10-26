namespace MapCue.DemoApp.Pages;

/// <summary>Small view factory so every key element gets its automation id the same way.</summary>
internal static class Ui
{
    public static Label Title(string text, string automationId) => new()
    {
        Text = text,
        AutomationId = automationId,
        FontSize = 26,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 8, 0, 8),
    };

    public static Label Text(string text, string? automationId = null)
    {
        var label = new Label { Text = text, FontSize = 16 };
        if (automationId is not null)
        {
            label.AutomationId = automationId;
        }

        return label;
    }

    public static Button Button(string text, string? automationId, Func<Task> onClicked)
    {
        var button = new Button { Text = text, Margin = new Thickness(0, 4) };
        if (automationId is not null)
        {
            button.AutomationId = automationId;
        }

        // A second tap while the handler runs is ignored, so one tap is always one navigation.
        var busy = false;
        button.Clicked += async (_, _) =>
        {
            if (busy)
            {
                return;
            }

            busy = true;
            try
            {
                await onClicked();
            }
            finally
            {
                busy = false;
            }
        };
        return button;
    }

    public static Button Button(string text, string? automationId, Action onClicked) =>
        Button(text, automationId, () =>
        {
            onClicked();
            return Task.CompletedTask;
        });

    public static VerticalStackLayout Stack(params IView[] children)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(20), Spacing = 8 };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }
}

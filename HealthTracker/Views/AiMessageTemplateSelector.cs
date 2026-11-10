using HealthTracker.ViewModels;

namespace HealthTracker.Views;

/// <summary>Picks the user, assistant or error bubble template for a <see cref="ChatMessage"/>.</summary>
public class AiMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? ErrorTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not ChatMessage m) return AssistantTemplate!;
        if (m.IsError) return ErrorTemplate ?? AssistantTemplate!;
        return m.IsUser ? UserTemplate! : AssistantTemplate!;
    }
}

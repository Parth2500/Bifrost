using Bifrost.Core.Api;

namespace Bifrost.App.Desk;

/// <summary>Shows one question on the admin's screen.</summary>
public interface INotifier
{
    /// <summary>
    /// Shows the question until the admin picks a choice, dismisses it, or <paramref name="closeWhen"/>
    /// fires (the question closed elsewhere), which takes it off the screen.
    /// </summary>
    /// <returns>The chosen choice's id, or null when dismissed or closed.</returns>
    Task<string?> ShowAsync(Guid questionId, string title, string text, IReadOnlyList<Choice> choices, CancellationToken closeWhen);
}

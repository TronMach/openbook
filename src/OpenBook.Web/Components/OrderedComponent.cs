using Microsoft.AspNetCore.Components;

namespace OpenBook.Web.Components;

/// <summary>
/// Every component's base class (<c>@inherits</c> in <c>Components/_Imports.razor</c>; layouts use
/// <see cref="OrderedLayout"/>). <see cref="ComponentBase"/> renders as soon as <c>OnInitializedAsync</c> first waits,
/// which starts the children and draws the markup before the component's data is in. SQLite hides that, because its
/// calls finish without waiting. On PostgreSQL they really wait, so a static page's components would all use the
/// request's one DbContext at once ("a second operation was started on this context") and pages would draw null data.
/// Here a component renders only after it has loaded, whichever database is underneath, and in a static render the
/// components of a request load and handle their forms one at a time (<see cref="RenderTurn"/>), so pages can simply
/// inject services. Interactive components need no turns: their work goes through <c>Operations</c>, a scope each.
/// </summary>
public abstract class OrderedComponent : ComponentBase, IHandleEvent
{
    private bool _initialized;

    [Inject]
    private RenderTurn Turn { get; set; } = null!;

    public override Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this); // a ParameterView can only be read before the first wait
        return LoadThenRenderAsync();
    }

    private async Task LoadThenRenderAsync()
    {
        var takeTurn = !RendererInfo.IsInteractive;
        if (takeTurn)
            await Turn.WaitAsync();
        try
        {
            if (!_initialized)
            {
                _initialized = true;
                OnInitialized();
                await OnInitializedAsync();
            }

            OnParametersSet();
            await OnParametersSetAsync();
        }
        finally
        {
            if (takeTurn)
                Turn.Release();
        }

        StateHasChanged();
    }

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) =>
        RendererInfo.IsInteractive ? HandleAsync(callback, arg) : HandleInTurnAsync(callback, arg);

    // What ComponentBase does: a render after the first part, so a form can show it's busy, and one at the end.
    private async Task HandleAsync(EventCallbackWorkItem callback, object? arg)
    {
        var task = callback.InvokeAsync(arg);
        if (!task.IsCompleted)
            StateHasChanged();
        await task;
        StateHasChanged();
    }

    private async Task HandleInTurnAsync(EventCallbackWorkItem callback, object? arg)
    {
        // A handler that raises its parent's callback is already in its turn; waiting for it would never end.
        var alreadyInTurn = Turn.InHandler;
        if (!alreadyInTurn)
        {
            await Turn.WaitAsync();
            Turn.InHandler = true;
        }

        try
        {
            await callback.InvokeAsync(arg);
        }
        finally
        {
            if (!alreadyInTurn)
            {
                Turn.InHandler = false;
                Turn.Release();
            }
        }

        StateHasChanged();
    }
}

/// <summary><see cref="OrderedComponent"/> for layouts: what <see cref="LayoutComponentBase"/> adds is <c>Body</c>.</summary>
public abstract class OrderedLayout : OrderedComponent
{
    [Parameter]
    public RenderFragment? Body { get; set; }
}

/// <summary>Whose turn it is to use the request's services in a static render. One per scope.</summary>
public sealed class RenderTurn : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly AsyncLocal<bool> _inHandler = new();

    /// <summary>True inside an event handler that has the turn, and in anything it awaits.</summary>
    internal bool InHandler
    {
        get => _inHandler.Value;
        set => _inHandler.Value = value;
    }

    internal Task WaitAsync() => _turn.WaitAsync();

    internal void Release() => _turn.Release();

    public void Dispose() => _turn.Dispose();
}

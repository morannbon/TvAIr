using TvAIrPlugin.Runtime;

namespace TvAIrPlugin.Windows;

public enum PluginWindowMultiplicity { Single, Multiple }
public enum PluginWindowActivationMode { None, ShowWithoutActivation, Activate, RevealWithoutActivation }
/// <summary>Runtime WindowのHost正本ライフサイクル状態。Host-managed ToolWindowの終端はRuntimeWindowLifecycleChangedでPluginへ通知される。</summary>
public enum PluginWindowLifecycleState { Created, Shown, Hidden, Closing, Closed }
public enum PluginWindowScrollPolicy { Auto, Hidden, Vertical, Horizontal, Both }
public enum PluginWindowAxisScrollPolicy { Auto, Hidden, Visible }
public enum PluginWindowSizeReference { OuterWindow, ContentArea }
public enum PluginWindowResizeMode { Fixed, Vertical, Horizontal, Both }
public enum PluginWindowRefreshMode { None, StatePatch, Rerender, Navigate }
public enum PluginWindowContentSizePolicy { Ignore, InitialOnly, GrowOnly, FitUntilUserResize }
public enum PluginWindowReusePolicy { SingleInstance, PerRoute, Multiple }
public enum PluginWindowActivationPolicy { ManualOpenOnly, Always, Never }
public enum PluginWindowCloseBehavior { Dispose, Hide, PreserveSession }
public enum PluginWindowBackgroundExecution { StopWithWindow, Continue }
public enum PluginWindowStatePersistence { None, Placement, PlacementAndUiState }

public sealed record PluginWindowSize(double Width, double Height);
public sealed record PluginWindowDefinition
{
    public required string WindowDefinitionId { get; init; }
    public required string Title { get; init; }
    public PluginWindowMultiplicity Multiplicity { get; init; }
    public PluginWindowSize InitialSize { get; init; } = new(620, 760);
    public PluginWindowSize MinimumSize { get; init; } = new(1, 1);
    public bool Resizable { get; init; } = true;
    public bool ShowInTaskbar { get; init; }
    public bool RememberPlacement { get; init; } = true;
    /// <summary>互換用の一括指定。Horizontal/VerticalがAutoのときだけ展開される。</summary>
    public PluginWindowScrollPolicy ScrollPolicy { get; init; } = PluginWindowScrollPolicy.Auto;
    public PluginWindowAxisScrollPolicy HorizontalScrollPolicy { get; init; } = PluginWindowAxisScrollPolicy.Auto;
    public PluginWindowAxisScrollPolicy VerticalScrollPolicy { get; init; } = PluginWindowAxisScrollPolicy.Auto;
    /// <summary>
    /// InitialSize/MinimumSizeおよび保存placementのサイズ基準を宣言する。
    /// Hostは異なるSizeReferenceで保存されたplacementをそのまま復元してはならない。
    /// Host管理ToolWindowのcontent controlはSizeReferenceに関係なくclient領域を常時完全占有する。
    /// Plugin側へHost非client寸法の固定補正を要求しない。
    /// </summary>
    public PluginWindowSizeReference SizeReference { get; init; } = PluginWindowSizeReference.OuterWindow;
    public PluginWindowResizeMode ResizeMode { get; init; } = PluginWindowResizeMode.Both;
    public PluginWindowRefreshMode RefreshMode { get; init; } = PluginWindowRefreshMode.Navigate;
    /// <summary>描画後の内容寸法をHost外形へ反映する方針。既定は勝手にサイズ変更しない。</summary>
    public PluginWindowContentSizePolicy ContentSizePolicy { get; init; } = PluginWindowContentSizePolicy.Ignore;
    /// <summary>
    /// Host-managed refreshでユーザーのviewport/scrollなどHostが所有するinteraction stateを保持する。
    /// wave/profile/選択項目などPlugin固有の意味状態はPluginが所有し、Hostは別表示コンテキストへ流用しない。
    /// </summary>
    public bool PreserveInteractionState { get; init; } = true;
    public PluginWindowReusePolicy ReusePolicy { get; init; } = PluginWindowReusePolicy.PerRoute;
    public PluginWindowActivationPolicy ActivationPolicy { get; init; } = PluginWindowActivationPolicy.ManualOpenOnly;
    public PluginWindowCloseBehavior CloseBehavior { get; init; } = PluginWindowCloseBehavior.Dispose;
    /// <summary>StopWithWindowではHostのClosing/Closed通知をterminal契機としてPlugin側background stateを停止する。</summary>
    public PluginWindowBackgroundExecution BackgroundExecution { get; init; } = PluginWindowBackgroundExecution.StopWithWindow;
    public PluginWindowStatePersistence StatePersistence { get; init; } = PluginWindowStatePersistence.Placement;
}
public sealed record CreatePluginWindowRequest
{
    public required string WindowDefinitionId { get; init; }
    public string InstanceKey { get; init; } = "default";
    public string? TitleOverride { get; init; }
    public PluginWindowActivationMode ActivationMode { get; init; } = PluginWindowActivationMode.ShowWithoutActivation;
}
public sealed record PluginWindowState
{
    public required string WindowDefinitionId { get; init; }
    public required string WindowInstanceId { get; init; }
    public required string InstanceKey { get; init; }
    public required string Title { get; init; }
    public PluginWindowLifecycleState LifecycleState { get; init; }
    public bool IsFocused { get; init; }
    public IReadOnlyList<string> AttachedSurfaceIds { get; init; } = Array.Empty<string>();
}
public interface ITvAirPluginWindowsApi
{
    TvAirOperationResult<PluginWindowState> Create(CreatePluginWindowRequest request);
    TvAirOperationResult Show(string windowInstanceId, bool activate = false);
    TvAirOperationResult Hide(string windowInstanceId);
    TvAirOperationResult Reveal(string windowInstanceId);
    TvAirOperationResult Activate(string windowInstanceId);
    TvAirOperationResult Close(string windowInstanceId);
    TvAirOperationResult<PluginWindowState> Get(string windowInstanceId);
    IReadOnlyList<PluginWindowState> List();
    TvAirOperationResult RefreshToolWindow(global::TvAIrPlugin.TvAirToolWindowRefreshRequestDto request);
    TvAirOperationResult<global::TvAIrPlugin.TvAirToolWindowStatePatchResultDto> PatchToolWindow(global::TvAIrPlugin.TvAirToolWindowStatePatchRequestDto request);
    TvAirOperationResult<global::TvAIrPlugin.TvAirToolWindowPlacementPersistenceResultDto> SetToolWindowPlacementPersistence(global::TvAIrPlugin.TvAirToolWindowPlacementPersistenceRequestDto request);
}

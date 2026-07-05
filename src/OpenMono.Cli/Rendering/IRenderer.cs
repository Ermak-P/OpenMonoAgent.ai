namespace OpenMono.Rendering;

/// <summary>
/// Объединяет возможности вывода, ввода и живой обратной связи в одном рендерере.
/// </summary>
/// <remarks>
/// Реализация этого интерфейса обычно выступает единой точкой взаимодействия с терминальным UI.
/// </remarks>
public interface IRenderer : IOutputSink, IInputReader, ILiveFeedback { }

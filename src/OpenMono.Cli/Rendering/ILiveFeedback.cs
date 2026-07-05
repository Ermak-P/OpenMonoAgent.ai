namespace OpenMono.Rendering;

/// <summary>
/// Определяет методы жизненного цикла для отображения живой обратной связи во время хода.
/// </summary>
public interface ILiveFeedback
{
    /// <summary>
    /// Запускает отображение состояния, связанного с началом нового хода.
    /// </summary>
    void BeginTurn();

    /// <summary>
    /// Завершает отображение состояния, связанного с текущим ходом.
    /// </summary>
    void EndTurn();
}

using System.Windows;

namespace DevezCode;

public partial class MainWindow
{
    /// <summary>현재 떠 있는 메인 창. 패널·서비스가 창 단위 오버레이/호스트를 호출할 때 사용.</summary>
    public static MainWindow? Current => Application.Current?.MainWindow as MainWindow;
}

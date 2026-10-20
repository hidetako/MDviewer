using System.Text;

namespace MdViewer;

internal static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    [STAThread]
    private static int Main(string[] args)
    {
        // Shift_JIS (CP932) など legacy エンコードの読み取りに必要
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // CLI: 関連付けのみ行って終了
        if (args.Length > 0 && (args[0] == "--register" || args[0] == "--unregister"))
        {
            try
            {
                if (args[0] == "--register") FileAssociation.Register();
                else FileAssociation.Unregister();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        var initialPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(initialPath));
        return 0;
    }
}

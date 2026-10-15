using System.IO;

namespace Pdv.Desktop.Services;

/// <summary>
/// Configuração da máquina em %ProgramData%\SistemaPDV, que sobrepõe o appsettings.json do programa
/// (Arquivos de Programas, sem permissão de escrita):
/// connection.json — endereço do servidor da loja e chave de acesso deste caixa (cifrada), gravado ao conectar;
/// appsettings.json — demais ajustes da máquina (impressora etc.), editado pelo técnico;
/// venda-em-andamento.json — diário da venda que está sendo concluída (proteção contra queda de energia).
/// A variável PDV_DATA_DIR troca a pasta (treinamento ou vários caixas de teste no mesmo computador).
/// </summary>
public static class LocalSettings
{
    public static string Folder { get; } = Environment.GetEnvironmentVariable("PDV_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SistemaPDV");

    public static string SettingsFilePath { get; } = Path.Combine(Folder, "appsettings.json");
}

using System.Globalization;
using System.Text;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Analises;

/// <summary>
/// Confere se o trecho citado pelo modelo existe de verdade na linha do tempo: nas falas (que podem ser citadas
/// atravessando dois trechos seguidos) ou na descrição de uma tela. A comparação ignora acentos, maiúsculas,
/// pontuação e espaços. Quando acha, o tempo da citação passa a ser o real (o modelo pode errar o tempo).
/// </summary>
public sealed class ConferenciaCitacoes
{
    /// <summary>Trechos muito curtos ("sim", "ok") aparecem em qualquer lugar e não provam nada.</summary>
    public const int MinimoDeCaracteres = 8;

    private readonly string falas;
    private readonly List<(int Posicao, EntradaLinhaDoTempo Entrada)> inicios = [];
    private readonly List<(string Normalizado, EntradaLinhaDoTempo Entrada)> telas = [];

    public ConferenciaCitacoes(Transcricao transcricao) : this(LinhaDoTempo.Montar(transcricao))
    {
    }

    public ConferenciaCitacoes(IEnumerable<EntradaLinhaDoTempo> linhaDoTempo)
    {
        var sb = new StringBuilder();
        foreach (var entrada in linhaDoTempo)
        {
            var normalizado = Normalizar(entrada.Texto);
            if (normalizado.Length == 0)
                continue;
            if (entrada.DaTela)
            {
                telas.Add((normalizado, entrada));
                continue;
            }
            inicios.Add((sb.Length, entrada));
            sb.Append(normalizado).Append(' ');
        }
        falas = sb.ToString();
    }

    public Citacao Conferir(string trecho, string? tempoInformado)
    {
        var limpo = trecho.Trim().Trim('"', '“', '”', '\'');
        var normalizado = Normalizar(limpo);

        if (normalizado.Length >= MinimoDeCaracteres)
        {
            var posicao = falas.IndexOf(normalizado, StringComparison.Ordinal);
            if (posicao >= 0)
                return new Citacao(limpo, FalaEm(posicao).Inicio, Conferida: true);

            // Na tela, prefere a mais próxima do tempo informado (a mesma mensagem pode aparecer em várias telas).
            var informado = LerTempo(tempoInformado);
            var tela = telas.Where(t => t.Normalizado.Contains(normalizado, StringComparison.Ordinal))
                .Select(t => t.Entrada)
                .OrderBy(e => informado is null ? TimeSpan.Zero : (e.Inicio - informado.Value).Duration())
                .FirstOrDefault();
            if (tela is not null)
                return new Citacao(limpo, tela.Inicio, Conferida: true, DaTela: true, Frame: tela.Frame);
        }

        return new Citacao(limpo, LerTempo(tempoInformado), Conferida: false);
    }

    private EntradaLinhaDoTempo FalaEm(int posicao)
    {
        var atual = inicios[0].Entrada;
        foreach (var (inicio, entrada) in inicios)
        {
            if (inicio > posicao)
                break;
            atual = entrada;
        }
        return atual;
    }

    /// <summary>Minúsculas, sem acentos, só letras e dígitos separados por um espaço.</summary>
    public static string Normalizar(string valor)
    {
        var sb = new StringBuilder(valor.Length);
        var espacoPendente = false;
        foreach (var c in valor.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (espacoPendente && sb.Length > 0)
                    sb.Append(' ');
                espacoPendente = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                espacoPendente = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Aceita "mm:ss", "m:ss", "h:mm:ss", com ou sem colchetes.</summary>
    public static TimeSpan? LerTempo(string? tempo)
    {
        if (string.IsNullOrWhiteSpace(tempo))
            return null;
        var limpo = tempo.Trim().Trim('[', ']');
        string[] formatos = [@"h\:mm\:ss", @"mm\:ss", @"m\:ss"];
        return TimeSpan.TryParseExact(limpo, formatos, CultureInfo.InvariantCulture, out var t) ? t : null;
    }
}

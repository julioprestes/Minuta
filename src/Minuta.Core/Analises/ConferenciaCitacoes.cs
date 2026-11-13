using System.Globalization;
using System.Text;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Analises;

/// <summary>
/// Confere se o trecho citado pelo modelo existe de verdade na transcrição.
/// A comparação ignora acentos, maiúsculas, pontuação e espaços, e aceita trechos que atravessam
/// dois segmentos. Quando acha, o tempo da citação passa a ser o do segmento real (o modelo pode errar o tempo).
/// </summary>
public sealed class ConferenciaCitacoes
{
    /// <summary>Trechos muito curtos ("sim", "ok") aparecem em qualquer lugar e não provam nada.</summary>
    public const int MinimoDeCaracteres = 8;

    private readonly string texto;
    private readonly List<(int Posicao, SegmentoTranscricao Segmento)> inicios = [];

    public ConferenciaCitacoes(Transcricao transcricao)
    {
        var sb = new StringBuilder();
        foreach (var segmento in transcricao.Segmentos)
        {
            var normalizado = Normalizar(segmento.Texto);
            if (normalizado.Length == 0)
                continue;
            inicios.Add((sb.Length, segmento));
            sb.Append(normalizado).Append(' ');
        }
        texto = sb.ToString();
    }

    public Citacao Conferir(string trecho, string? tempoInformado)
    {
        var limpo = trecho.Trim().Trim('"', '“', '”', '\'');
        var normalizado = Normalizar(limpo);

        if (normalizado.Length >= MinimoDeCaracteres)
        {
            var posicao = texto.IndexOf(normalizado, StringComparison.Ordinal);
            if (posicao >= 0)
                return new Citacao(limpo, SegmentoEm(posicao).Inicio, Conferida: true);
        }

        return new Citacao(limpo, LerTempo(tempoInformado), Conferida: false);
    }

    private SegmentoTranscricao SegmentoEm(int posicao)
    {
        var atual = inicios[0].Segmento;
        foreach (var (inicio, segmento) in inicios)
        {
            if (inicio > posicao)
                break;
            atual = segmento;
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

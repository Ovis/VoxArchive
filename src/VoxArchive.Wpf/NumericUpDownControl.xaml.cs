using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定値を範囲内で直接入力または増減できる軽量NumericUpDownを提供する
/// </summary>
/// <remarks>
/// 外部UIライブラリを追加せず、文字起こし設定で必要な整数・小数入力だけを共通化する。
/// 値確定時は範囲外を黙ってclampせず、直前の有効値へ戻すことで保存値と表示値の不一致を避ける。
/// </remarks>
public partial class NumericUpDownControl : UserControl
{
    private double _value;

    /// <summary>Controlを初期化する</summary>
    public NumericUpDownControl()
    {
        InitializeComponent();
        UpdateText();
    }

    /// <summary>最小値を取得・設定する</summary>
    public double Minimum { get; set; } = double.MinValue;

    /// <summary>最大値を取得・設定する</summary>
    public double Maximum { get; set; } = double.MaxValue;

    /// <summary>増減ボタン1回あたりの変化量を取得・設定する</summary>
    public double Increment { get; set; } = 1d;

    /// <summary>表示する小数桁数を取得・設定する</summary>
    public int DecimalPlaces { get; set; }

    /// <summary>数値の後ろへ表示する単位を取得・設定する</summary>
    public string UnitText
    {
        get => UnitTextBlock.Text;
        set => UnitTextBlock.Text = value ?? string.Empty;
    }

    /// <summary>現在値を取得・設定する</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value) || value < Minimum || value > Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"値は{Minimum}以上{Maximum}以下である必要があります。");
            }

            _value = value;
            UpdateText();
        }
    }

    private void OnDecreaseClick(object sender, RoutedEventArgs e)
        => ApplyStep(-Increment);

    private void OnIncreaseClick(object sender, RoutedEventArgs e)
        => ApplyStep(Increment);

    private void ApplyStep(double delta)
    {
        CommitTextOrRestore();
        var next = _value + delta;
        if (next < Minimum || next > Maximum)
        {
            return;
        }

        _value = next;
        UpdateText();
    }

    private void OnValueTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CommitTextOrRestore();

    private void OnValueTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitTextOrRestore();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitTextOrRestore()
    {
        if (TryParse(ValueTextBox.Text, out var parsed)
            && double.IsFinite(parsed)
            && parsed >= Minimum
            && parsed <= Maximum)
        {
            _value = parsed;
        }

        UpdateText();
    }

    private void UpdateText()
        => ValueTextBox.Text = _value.ToString($"F{Math.Max(0, DecimalPlaces)}", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
           || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

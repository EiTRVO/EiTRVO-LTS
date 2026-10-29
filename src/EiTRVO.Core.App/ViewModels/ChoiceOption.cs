namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 一个下拉选项：给用户看的 <see cref="Text"/> + 给代码用的 <see cref="Value"/>。
/// <para>
/// 有了它，ComboBox 就能用 <c>DisplayMemberPath="Text"</c> +
/// <c>SelectedValuePath="Value"</c> 双向绑定到 ViewModel 上的强类型属性，
/// 而不必在 View 里做「下标 ↔ 枚举」的手工换算。
/// </para>
/// </summary>
/// <typeparam name="T">值的类型。</typeparam>
/// <param name="Text">显示文本。</param>
/// <param name="Value">实际值。</param>
internal sealed record ChoiceOption<T>(string Text, T Value);

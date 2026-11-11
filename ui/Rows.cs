using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CadReviewUI;

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class ItemRow : Notify
{
    // Stored in existing confirmation fields so an intentional clear survives a
    // session round trip. It is not a CAD value: callers must block review/export
    // while HasClearedOverride is true.
    public const string ClearOverride = "\u0001CADREVIEW:CLEAR_OVERRIDE:V1\u0002";
    public const string AutoEditNote = "在表格中手动修改字段（自动记录）";
    public const string AutoAdoptNote = "在界面选择采用图纸识别值（自动记录）";
    public const string AutoExcludeNote = "在界面选择不纳入（未记录具体原因）";
    public const string AutoCrossedIncludeNote = "用户对照原图后在界面直接将图面打X明细改为纳入（自动记录）";

    public DsItem Src { get; }
    public string FileName { get; }
    public event Action<ItemRow>? Edited;

    string _mode = Rules.Pending, _num = "", _name = "", _mat = "", _trt = "", _qty = "", _unit = "", _note = "";
    string _error = "";
    bool _quiet;

    public ItemRow(DsItem src, string fileName, ItemDecision d)
    {
        Src = src; FileName = fileName;
        if (new[] { OrigDrawing, OrigName, OrigMaterial, OrigTreatment, OrigQuantity, OrigUnit }
            .Any(value => value != null && value.Contains(ClearOverride, StringComparison.Ordinal)))
            throw new InvalidDataException("识别原值包含复核保留标记，无法安全显示。");
        Apply(d);
    }

    public void Apply(ItemDecision d)
    {
        bool wasQuiet = _quiet;
        _quiet = true;
        try
        {
            Mode = d.Mode; ConfDrawing = d.DrawingNumber; ConfName = d.Name; ConfMaterial = d.Material;
            ConfTreatment = d.Treatment; ConfQuantity = d.Quantity; ConfUnit = d.Unit; Note = d.Note;
        }
        finally { _quiet = wasQuiet; }
        RaiseEditableFields();
    }

    public ItemDecision ToDecision() => new()
    {
        Mode = Mode, DrawingNumber = ConfDrawing.Trim(), Name = ConfName.Trim(), Material = ConfMaterial.Trim(),
        Treatment = ConfTreatment.Trim(), Quantity = ConfQuantity.Trim(), Unit = ConfUnit.Trim(), Note = Note.Trim(),
    };

    // —— 原值（只读）——
    public string Id => Src.Id;
    public string FileId => Src.FileId;
    public string OrigDrawing => Src.Cad.DrawingNumber;
    public string OrigName => Src.Cad.Name ?? "";
    public string OrigMaterial => Src.Cad.Material ?? "";
    public string OrigTreatment => Src.Cad.Treatment ?? "";
    public string OrigQuantity => Src.Cad.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string OrigUnit => Src.Cad.Unit ?? "";
    public string Warnings => Src.Warnings;
    public string QuantityEvidence => Src.QuantityEvidence;
    public string TreatmentEvidence => Src.TreatmentEvidence;
    public string ParentsText => string.Join("；", Src.Parents);
    public string ReferenceInfo => Src.ReferenceInfo;
    // Only the engine's explicit default decision marks an entirely crossed
    // drawing; do not infer that status from arbitrary warning text.
    public bool IsCrossedOut => Src.DefaultDecision is { Mode: Rules.Exclude, Note: string note } &&
        note.TrimStart().StartsWith("图面整框打X", StringComparison.Ordinal);
    public Visibility CrossedOutVisibility => IsCrossedOut ? Visibility.Visible : Visibility.Collapsed;
    public bool HasDuplicate => DuplicateQuantity.IsDuplicate(this);
    public string DuplicateLabel => HasDuplicate ? $"重复 {Src.Duplicate!.Count} 处" : "";
    public Visibility DuplicateVisibility => HasDuplicate ? Visibility.Visible : Visibility.Collapsed;
    public string DuplicateDetails => DuplicateQuantity.Describe(this);
    public bool HasCrossedOutInclusionReason
    {
        get
        {
            if (!IsCrossedOut) return true;
            string note = Note.Trim();
            return Rules.Meaningful(note) && !Rules.LooksUnresolved(note) &&
                !string.Equals(note, Src.DefaultDecision?.Note?.Trim(), StringComparison.Ordinal) &&
                note is not (AutoEditNote or AutoAdoptNote or AutoExcludeNote);
        }
    }
    public bool NeedsCrossedOutInclusionReason
    {
        get
        {
            if (!IsCrossedOut || Mode is not (Rules.Adopt or Rules.Revise)) return false;
            return !HasCrossedOutInclusionReason;
        }
    }
    public string KindText => Src.Kind switch
    {
        "part_title" => "零件",
        "assembly_title" => "组件/父图",
        "subpart" => "子件",
        _ => Src.Kind,
    };
    public bool IsCompleteOriginal => Rules.Complete(Src.Cad);
    public string MissingText => string.Join("、", Rules.MissingFields(Src.Cad));

    // —— 人工确认（可编辑）——
    public string Mode
    {
        get => _mode;
        set
        {
            if (!_quiet && IsCrossedOut && (value is Rules.Adopt or Rules.Revise) && !HasCrossedOutInclusionReason)
            {
                Raise(nameof(Mode));
                return;
            }
            if (!Set(ref _mode, value)) return;
            if (!_quiet)
            {
                // Replace only our own factual action records, never a prior
                // user-written explanation. The final confirmation is separate.
                if ((_note == AutoEditNote && value != Rules.Revise) ||
                    (_note == AutoAdoptNote && value != Rules.Adopt) ||
                    (_note == AutoExcludeNote && value != Rules.Exclude))
                    Note = "";
                if (string.IsNullOrWhiteSpace(_note))
                {
                    if (value == Rules.Revise && HasOverride) Note = AutoEditNote;
                    else if (value == Rules.Adopt && Src.RequiresReview) Note = AutoAdoptNote;
                    else if (value == Rules.Exclude)
                        Note = IsCrossedOut && !string.IsNullOrWhiteSpace(Src.DefaultDecision?.Note)
                            ? Src.DefaultDecision!.Note : AutoExcludeNote;
                }
            }
            Raise(nameof(NeedsCrossedOutInclusionReason));
            Touched();
        }
    }
    public string ConfDrawing { get => _num; set => SetConfirmation(ref _num, value, nameof(EditDrawing)); }
    public string ConfName { get => _name; set => SetConfirmation(ref _name, value, nameof(EditName)); }
    public string ConfMaterial { get => _mat; set => SetConfirmation(ref _mat, value, nameof(EditMaterial)); }
    public string ConfTreatment { get => _trt; set => SetConfirmation(ref _trt, value, nameof(EditTreatment)); }
    public string ConfQuantity { get => _qty; set => SetConfirmation(ref _qty, value, nameof(EditQuantity)); }
    public string ConfUnit { get => _unit; set => SetConfirmation(ref _unit, value, nameof(EditUnit)); }
    public string Note { get => _note; set { if (Set(ref _note, value ?? "")) { Raise(nameof(HasCrossedOutInclusionReason)); Raise(nameof(NeedsCrossedOutInclusionReason)); Touched(); } } }

    // These expose the current value while keeping Src.Cad immutable. An empty
    // confirmation means inherit the original; ClearOverride means the user
    // deliberately cleared a nonempty original and must resolve it before export.
    public string EditDrawing { get => Effective(OrigDrawing, ConfDrawing); set => EditValue(value, OrigDrawing, ConfDrawing, v => ConfDrawing = v); }
    public string EditName { get => Effective(OrigName, ConfName); set => EditValue(value, OrigName, ConfName, v => ConfName = v); }
    public string EditMaterial { get => Effective(OrigMaterial, ConfMaterial); set => EditValue(value, OrigMaterial, ConfMaterial, v => ConfMaterial = v); }
    public string EditTreatment { get => Effective(OrigTreatment, ConfTreatment); set => EditValue(value, OrigTreatment, ConfTreatment, v => ConfTreatment = v); }
    public string EditQuantity { get => Effective(OrigQuantity, ConfQuantity); set => EditValue(value, OrigQuantity, ConfQuantity, v => ConfQuantity = v, quantity: true); }
    public string EditUnit { get => Effective(OrigUnit, ConfUnit); set => EditValue(value, OrigUnit, ConfUnit, v => ConfUnit = v); }

    public bool HasOverride =>
        _num.Trim() != "" || _name.Trim() != "" || _mat.Trim() != "" || _trt.Trim() != "" || _qty.Trim() != "" || _unit.Trim() != "";

    public bool HasClearedOverride =>
        IsClear(_num) || IsClear(_name) || IsClear(_mat) || IsClear(_trt) || IsClear(_qty) || IsClear(_unit);

    public IReadOnlyList<string> ClearedFieldNames
    {
        get
        {
            var fields = new List<string>();
            if (IsClear(_num)) fields.Add("图号");
            if (IsClear(_name)) fields.Add("部品名");
            if (IsClear(_mat)) fields.Add("材质");
            if (IsClear(_trt)) fields.Add("表面处理");
            if (IsClear(_qty)) fields.Add("数量");
            if (IsClear(_unit)) fields.Add("单位");
            return fields;
        }
    }

    static bool IsClear(string value) => string.Equals(value.Trim(), ClearOverride, StringComparison.Ordinal);

    static string Effective(string original, string confirmation) =>
        IsClear(confirmation) ? "" : string.IsNullOrWhiteSpace(confirmation) ? original : confirmation;

    void EditValue(string? value, string original, string current, Action<string> assign, bool quantity = false)
    {
        string input = value ?? "";
        if (input.Contains(ClearOverride, StringComparison.Ordinal))
            throw new ArgumentException("复核保留标记不能作为字段值输入。", nameof(value));
        string trimmed = input.Trim();
        string source = original.Trim();
        bool same = string.Equals(trimmed, source, StringComparison.Ordinal) ||
            (quantity && SameQuantity(trimmed, source));
        string next = same ? "" : trimmed.Length == 0 ? ClearOverride : input;
        if (string.Equals(current, next, StringComparison.Ordinal)) return;
        assign(next);
        // This records only the edit, not a claim that the drawing was checked.
        // An excluded row still requires a real exclusion reason from the caller.
        if (!_quiet && _mode == Rules.Revise && HasOverride && string.IsNullOrWhiteSpace(_note))
            Note = AutoEditNote;
    }

    // Decimal.TryParse can round long inputs. Compare canonical digits as well as
    // the decimal value so a rounded input is never mistaken for the original.
    static bool SameQuantity(string input, string original)
    {
        if (!decimal.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !decimal.TryParse(original, NumberStyles.Float, CultureInfo.InvariantCulture, out var source) || value != source ||
            !TryCanonicalQuantity(input, out var inputDigits, out var inputExponent) ||
            !TryCanonicalQuantity(value.ToString("G29", CultureInfo.InvariantCulture), out var valueDigits, out var valueExponent))
            return false;
        return inputDigits == valueDigits && inputExponent == valueExponent;
    }

    static bool TryCanonicalQuantity(string text, out string digits, out long exponent)
    {
        digits = "";
        exponent = 0;
        int e = text.IndexOfAny(new[] { 'e', 'E' });
        string mantissa = e < 0 ? text : text[..e];
        if (e >= 0 && !long.TryParse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) return false;
        if (mantissa.StartsWith('+') || mantissa.StartsWith('-')) mantissa = mantissa[1..];
        int point = mantissa.IndexOf('.');
        int fractional = point < 0 ? 0 : mantissa.Length - point - 1;
        digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) { digits = "0"; exponent = 0; return true; }
        int end = digits.Length;
        while (end > 0 && digits[end - 1] == '0') end--;
        try { exponent = checked(exponent - fractional + (digits.Length - end)); }
        catch (OverflowException) { return false; }
        digits = digits[..end];
        return true;
    }

    void SetConfirmation(ref string field, string? value, string editableProperty,
        [CallerMemberName] string? propertyName = null)
    {
        string next = value ?? "";
        if (next.Contains(ClearOverride, StringComparison.Ordinal) && !IsClear(next))
            throw new ArgumentException("复核保留标记不能作为字段值的一部分。", nameof(value));
        bool hadOverride = HasOverride;
        if (!Set(ref field, next, propertyName)) return;
        Raise(editableProperty);
        Raise(nameof(HasOverride));
        Raise(nameof(HasClearedOverride));
        Raise(nameof(ClearedFieldNames));
        Revised(hadOverride);
    }

    void RaiseEditableFields()
    {
        Raise(nameof(EditDrawing)); Raise(nameof(EditName)); Raise(nameof(EditMaterial));
        Raise(nameof(EditTreatment)); Raise(nameof(EditQuantity)); Raise(nameof(EditUnit));
        Raise(nameof(HasOverride)); Raise(nameof(HasClearedOverride)); Raise(nameof(ClearedFieldNames));
    }

    // A changed confirmation needs review; undoing the last override cannot
    // silently mark the original as confirmed. Exclusions stay excluded.
    void Revised(bool hadOverride)
    {
        if (!_quiet)
        {
            if (HasOverride && (_mode == Rules.Pending || _mode == Rules.Adopt)) Mode = Rules.Revise;
            else if (hadOverride && !HasOverride)
            {
                if (_mode != Rules.Exclude) Mode = Rules.Pending;
                if (string.Equals(_note, AutoEditNote, StringComparison.Ordinal)) Note = "";
            }
        }
        Touched();
    }

    void Touched()
    {
        Raise(nameof(Mode)); // 让行底色刷新
        if (!_quiet) Edited?.Invoke(this);
    }

    // —— 校验问题 ——
    public string ErrorText { get => _error; set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(ErrorVisibility)); } } }
    public bool HasError => _error != "";
    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class FileRow : Notify
{
    public DsFile Src { get; }
    public event Action<FileRow>? Edited;
    string _state = "待核查", _note = "", _pc = "", _error = "", _summary = "";

    public FileRow(DsFile src, FileDecision d) { Src = src; Apply(d); }

    public void Apply(FileDecision d) { _state = d.State; _note = d.Note; _pc = d.ParentChildNote; Raise(nameof(State)); Raise(nameof(Note)); Raise(nameof(ParentChildNote)); }
    public FileDecision ToDecision() => new() { State = State, Note = Note.Trim(), ParentChildNote = ParentChildNote.Trim() };

    public string Id => Src.Id;
    public string Name => Src.Name;
    public string Path => Src.Path;
    public string Diagnostics => Src.Diagnostics;
    public bool RequiresAcknowledgement => Src.RequiresAcknowledgement;

    public string State { get => _state; set { if (Set(ref _state, value)) Edited?.Invoke(this); } }
    public string Note { get => _note; set { if (Set(ref _note, value ?? "")) Edited?.Invoke(this); } }
    public string ParentChildNote { get => _pc; set { if (Set(ref _pc, value ?? "")) Edited?.Invoke(this); } }

    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string ErrorText { get => _error; set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(ErrorVisibility)); } } }
    public bool HasError => _error != "";
    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;
    public string Title => Id + "  " + Name;
    public override string ToString() => Title;
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Starling.Dom;

public sealed record FormEntry(string Name, string Value);

public sealed record FormValidityState(
    bool ValueMissing,
    bool TypeMismatch,
    bool PatternMismatch,
    bool TooLong,
    bool TooShort,
    bool RangeUnderflow,
    bool RangeOverflow,
    bool StepMismatch,
    bool BadInput,
    bool CustomError)
{
    public bool Valid => !(ValueMissing || TypeMismatch || PatternMismatch || TooLong || TooShort
        || RangeUnderflow || RangeOverflow || StepMismatch || BadInput || CustomError);
}

public static class HtmlFormControls
{
    private static readonly HashSet<string> TextInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text", "search", "email", "url", "tel", "password", "number",
    };

    public static bool IsFormControl(Element element) => element.LocalName switch
    {
        "button" or "fieldset" or "input" or "object" or "output" or "select" or "textarea" => true,
        _ => false,
    };

    public static bool IsTextControl(Element element)
        => element.LocalName == "textarea"
            || (element.LocalName == "input" && TextInputTypes.Contains(InputType(element)));

    public static string InputType(Element element)
        => element.LocalName == "button"
            ? (element.GetAttribute("type") ?? "submit").Trim().ToLowerInvariant()
            : (element.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();

    public static string Value(Element element) => element.LocalName switch
    {
        "textarea" => element.InputValue ?? element.TextContent,
        "select" => SelectValue(element),
        "option" => OptionValue(element),
        "button" => element.GetAttribute("value") ?? element.TextContent.Trim(),
        _ => element.InputValue ?? element.GetAttribute("value") ?? string.Empty,
    };

    public static void SetValue(Element element, string value)
    {
        if (element.LocalName == "select")
        {
            SelectOptionByValue(element, value);
        }
        else
        {
            element.InputValue = value;
        }

        if (IsTextControl(element))
        {
            SetSelectionRange(element, value.Length, value.Length, "none");
        }
    }

    public static bool Checked(Element element)
        => element.LocalName == "input" && element.HasAttribute("checked");

    public static void SetChecked(Element element, bool value)
    {
        if (element.LocalName != "input")
        {
            return;
        }

        if (!value)
        {
            element.RemoveAttribute("checked");
            return;
        }

        element.SetAttribute("checked", string.Empty);
        if (InputType(element) != "radio")
        {
            return;
        }

        var name = element.GetAttribute("name") ?? string.Empty;
        if (name.Length == 0)
        {
            return;
        }

        var owner = FormOwner(element);
        foreach (var other in element.OwnerDocument?.DescendantElements() ?? Root(element).DescendantElements())
        {
            if (ReferenceEquals(other, element))
            {
                continue;
            }

            if (other.LocalName != "input" || InputType(other) != "radio")
            {
                continue;
            }

            if (!string.Equals(other.GetAttribute("name") ?? string.Empty, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ReferenceEquals(FormOwner(other), owner))
            {
                continue;
            }

            other.RemoveAttribute("checked");
        }
    }

    public static Element? FormOwner(Element element)
    {
        var formId = element.GetAttribute("form");
        if (!string.IsNullOrEmpty(formId)
            && element.OwnerDocument?.GetElementById(formId) is { LocalName: "form" } explicitOwner)
        {
            return explicitOwner;
        }

        for (Node? n = element.ParentNode; n is not null; n = n.ParentNode)
        {
            if (n is Element { LocalName: "form" } form)
            {
                return form;
            }
        }

        return null;
    }

    public static IReadOnlyList<Element> FormControls(Element form)
    {
        if (form.LocalName != "form")
        {
            return Array.Empty<Element>();
        }

        var results = new List<Element>();
        foreach (var element in form.DescendantElements())
        {
            if (IsFormControl(element) && ReferenceEquals(FormOwner(element), form))
            {
                results.Add(element);
            }
        }

        var id = form.GetAttribute("id");
        if (form.OwnerDocument is not null && !string.IsNullOrEmpty(id))
        {
            foreach (var element in form.OwnerDocument.DescendantElements())
            {
                if (!IsFormControl(element))
                {
                    continue;
                }

                if (!string.Equals(element.GetAttribute("form"), id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!results.Contains(element))
                {
                    results.Add(element);
                }
            }
        }
        return results;
    }

    public static IReadOnlyList<FormEntry> ConstructEntryList(Element form, Element? submitter = null)
    {
        if (form.LocalName != "form")
        {
            return Array.Empty<FormEntry>();
        }

        var entries = new List<FormEntry>();
        foreach (var control in FormControls(form))
        {
            AppendEntries(entries, control, submitter);
        }

        return entries;
    }

    public static string UrlEncodedFormData(Element form)
    {
        var entries = ConstructEntryList(form);
        var parts = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            parts.Add(Uri.EscapeDataString(entry.Name).Replace("%20", "+", StringComparison.Ordinal)
                + "="
                + Uri.EscapeDataString(entry.Value).Replace("%20", "+", StringComparison.Ordinal));
        }
        return string.Join("&", parts);
    }

    public static FormValidityState Validity(Element element)
    {
        if (!WillValidate(element))
        {
            return new FormValidityState(false, false, false, false, false, false, false, false, false, false);
        }

        var value = Value(element);
        var missing = element.HasAttribute("required") && ValueMissing(element, value);
        var typeMismatch = TypeMismatch(element, value);
        var patternMismatch = PatternMismatch(element, value);
        var tooLong = LengthMismatch(element, value, "maxlength", over: true);
        var tooShort = LengthMismatch(element, value, "minlength", over: false);
        var rangeUnderflow = RangeMismatch(element, value, "min", under: true);
        var rangeOverflow = RangeMismatch(element, value, "max", under: false);
        var customError = !string.IsNullOrEmpty(element.CustomValidationMessage);
        return new FormValidityState(
            missing, typeMismatch, patternMismatch, tooLong, tooShort,
            rangeUnderflow, rangeOverflow, false, false, customError);
    }

    public static bool CheckValidity(Element element)
    {
        if (element.LocalName == "form")
        {
            foreach (var control in FormControls(element))
            {
                if (!Validity(control).Valid)
                {
                    return false;
                }
            }

            return true;
        }
        return Validity(element).Valid;
    }

    public static bool WillValidate(Element element)
    {
        if (element.HasAttribute("disabled"))
        {
            return false;
        }

        return element.LocalName switch
        {
            "textarea" or "select" => true,
            "input" => InputType(element) is not ("hidden" or "button" or "submit" or "reset" or "image" or "file"),
            _ => false,
        };
    }

    public static string ValidationMessage(Element element)
    {
        if (!string.IsNullOrEmpty(element.CustomValidationMessage))
        {
            return element.CustomValidationMessage;
        }

        var validity = Validity(element);
        if (validity.Valid)
        {
            return string.Empty;
        }

        if (validity.ValueMissing)
        {
            return "Please fill out this field.";
        }

        if (validity.TypeMismatch)
        {
            return "Please enter a valid value.";
        }

        if (validity.PatternMismatch)
        {
            return "Please match the requested format.";
        }

        if (validity.TooLong)
        {
            return "Please shorten this text.";
        }

        if (validity.TooShort)
        {
            return "Please lengthen this text.";
        }

        if (validity.RangeUnderflow)
        {
            return "Please enter a larger value.";
        }

        if (validity.RangeOverflow)
        {
            return "Please enter a smaller value.";
        }

        return "Please enter a valid value.";
    }

    public static void SetSelectionRange(Element element, int start, int end, string direction)
    {
        if (!IsTextControl(element))
        {
            return;
        }

        var length = Value(element).Length;
        var s = Math.Clamp(start, 0, length);
        var e = Math.Clamp(end, 0, length);
        if (e < s)
        {
            s = e;
        }

        element.SelectionStart = s;
        element.SelectionEnd = e;
        element.SelectionDirection = direction is "forward" or "backward" ? direction : "none";
    }

    public static IReadOnlyList<string> AutocompleteSuggestions(Element element)
    {
        var values = new List<string>();
        if (element.GetAttribute("list") is { Length: > 0 } listId
            && element.OwnerDocument?.GetElementById(listId) is { LocalName: "datalist" } datalist)
        {
            foreach (var option in datalist.DescendantElements())
            {
                if (option.LocalName != "option")
                {
                    continue;
                }

                var value = OptionValue(option);
                if (value.Length > 0 && !values.Contains(value, StringComparer.Ordinal))
                {
                    values.Add(value);
                }
            }
        }

        var name = element.GetAttribute("name");
        if (!string.IsNullOrWhiteSpace(name) && AllowsAutocomplete(element))
        {
            foreach (var value in element.OwnerDocument?.GetAutocompleteValues(name) ?? Array.Empty<string>())
            {
                if (!values.Contains(value, StringComparer.Ordinal))
                {
                    values.Add(value);
                }
            }
        }
        return values;
    }

    public static void RecordAutocompleteSubmission(Element form)
    {
        if (form.LocalName != "form" || form.OwnerDocument is not { } document)
        {
            return;
        }

        foreach (var control in FormControls(form))
        {
            if (!AllowsAutocomplete(control))
            {
                continue;
            }

            var name = control.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            document.RecordAutocompleteValue(name, Value(control));
        }
    }

    private static void AppendEntries(List<FormEntry> entries, Element control, Element? submitter)
    {
        if (control.HasAttribute("disabled"))
        {
            return;
        }

        var name = control.GetAttribute("name");
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        switch (control.LocalName)
        {
            case "input":
                var type = InputType(control);
                if (type is "button" or "reset" or "file" or "image")
                {
                    return;
                }

                if (type == "submit" && !ReferenceEquals(control, submitter))
                {
                    return;
                }

                if (type is "checkbox" or "radio")
                {
                    if (!Checked(control))
                    {
                        return;
                    }

                    entries.Add(new FormEntry(name, control.GetAttribute("value") ?? "on"));
                    return;
                }
                entries.Add(new FormEntry(name, Value(control)));
                return;
            case "textarea":
                entries.Add(new FormEntry(name, Value(control)));
                return;
            case "select":
                foreach (var option in SelectedOptions(control))
                {
                    entries.Add(new FormEntry(name, OptionValue(option)));
                }

                return;
            case "button":
                if (InputType(control) == "submit" && ReferenceEquals(control, submitter))
                {
                    entries.Add(new FormEntry(name, Value(control)));
                }

                return;
        }
    }

    private static bool AllowsAutocomplete(Element element)
    {
        if (element.GetAttribute("autocomplete")?.Equals("off", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        if (FormOwner(element)?.GetAttribute("autocomplete")?.Equals("off", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        return !(element.LocalName == "input" && InputType(element) == "password");
    }

    private static bool ValueMissing(Element element, string value)
    {
        if (element.LocalName == "input" && InputType(element) is "checkbox" or "radio")
        {
            return !Checked(element);
        }

        if (element.LocalName == "select")
        {
            return string.IsNullOrEmpty(SelectValue(element));
        }

        return string.IsNullOrEmpty(value);
    }

    private static bool TypeMismatch(Element element, string value)
    {
        if (string.IsNullOrEmpty(value) || element.LocalName != "input")
        {
            return false;
        }

        return InputType(element) switch
        {
            "email" => !value.Contains('@', StringComparison.Ordinal) || value.StartsWith("@", StringComparison.Ordinal)
                || value.EndsWith("@", StringComparison.Ordinal),
            "url" => !Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Scheme),
            _ => false,
        };
    }

    private static bool PatternMismatch(Element element, string value)
    {
        if (string.IsNullOrEmpty(value) || element.GetAttribute("pattern") is not { Length: > 0 } pattern)
        {
            return false;
        }

        try
        {
            return !Regex.IsMatch(value, "^(?:" + pattern + ")$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool LengthMismatch(Element element, string value, string attr, bool over)
    {
        if (!int.TryParse(element.GetAttribute(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            || limit < 0)
        {
            return false;
        }

        return over ? value.Length > limit : value.Length > 0 && value.Length < limit;
    }

    private static bool RangeMismatch(Element element, string value, string attr, bool under)
    {
        if (element.LocalName != "input" || InputType(element) != "number")
        {
            return false;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (!double.TryParse(element.GetAttribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit))
        {
            return false;
        }

        return under ? number < limit : number > limit;
    }

    private static string SelectValue(Element select)
    {
        foreach (var option in SelectedOptions(select))
        {
            return OptionValue(option);
        }

        return string.Empty;
    }

    private static void SelectOptionByValue(Element select, string value)
    {
        var matched = false;
        foreach (var option in select.DescendantElements())
        {
            if (option.LocalName != "option")
            {
                continue;
            }

            if (!matched && string.Equals(OptionValue(option), value, StringComparison.Ordinal))
            {
                option.SetAttribute("selected", string.Empty);
                matched = true;
            }
            else if (!select.HasAttribute("multiple"))
            {
                option.RemoveAttribute("selected");
            }
        }
    }

    private static IEnumerable<Element> SelectedOptions(Element select)
    {
        Element? first = null;
        var any = false;
        foreach (var option in select.DescendantElements())
        {
            if (option.LocalName != "option")
            {
                continue;
            }

            first ??= option;
            if (option.HasAttribute("selected"))
            {
                any = true;
                yield return option;
                if (!select.HasAttribute("multiple"))
                {
                    yield break;
                }
            }
        }
        if (!any && first is not null)
        {
            yield return first;
        }
    }

    private static string OptionValue(Element option)
        => option.GetAttribute("value") ?? option.TextContent.Trim();

    private static Node Root(Node node)
    {
        var root = node;
        while (root.ParentNode is not null)
        {
            root = root.ParentNode;
        }

        return root;
    }
}

﻿using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SmartMvvm.Avalonia.Xaml.Markup.Logic;

/// <summary>
/// Represents the base class for all logical operations.
/// </summary>
public abstract class LogicalBase : MarkupExtension, IBinding
{
    private IServiceProvider _serviceProvider;

    /// <summary>
    /// Creates a new instance of <see cref="LogicalBase"/>.
    /// </summary>
    /// <param name="items">Contains the input values.</param>
    protected LogicalBase(params object[] items)
    {
        Items = items;
    }

    /// <summary>
    /// Creates a new instance of <see cref="LogicalBase"/>.
    /// </summary>
    /// <param name="items">Contains the input values.</param>
    /// <remarks>Use this constructor if the list of items can be dynamic.</remarks>
    protected LogicalBase(IList<object> items)
    {
        Items = items;
    }

    /// <summary>
    /// Gets or sets an input value using the given <paramref name="index"/>.
    /// </summary>
    /// <param name="index">Index of the input value.</param>
    protected object this[int index]
    {
        get => Items[index];
        set => Items[index] = value;
    }

    /// <summary>
    /// Gets access to the underlying input values.
    /// </summary>
    protected IList<object> Items { get; private set; }

    /// <summary>
    /// Method that is invoked whenever any dependency (e.g. Binding) has changed to calculate the new resulting value.
    /// </summary>
    /// <param name="values">Represents the evaluated values for the given <see cref="Items"/>.</param>
    /// <returns>The result of the operation.</returns>
    protected abstract object Evaluate(IReadOnlyList<object> values);

    private void Dispatch(Processor processor, IServiceProvider serviceProvider)
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case LogicalBase logical:
                    logical.Dispatch(processor, serviceProvider);
                    break;

                case IBinding binding:
                    processor.PushBinding(binding);
                    break;

                case MarkupExtension extension:
                    processor.PushStaticObject(extension.ProvideValue(serviceProvider));
                    break;

                default:
                    processor.PushStaticObject(item);
                    break;
            }
        }

        processor.PushLogical(this);
    }

    /// <InheritDoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        return this;
    }

    /// <InheritDoc />
    public InstancedBinding Initiate(IAvaloniaObject target, AvaloniaProperty targetProperty, object anchor = null, bool enableDataValidation = false)
    {
        var processor = new Processor(_serviceProvider);

        Dispatch(processor, _serviceProvider);

        return processor.Execute(target, targetProperty, anchor, enableDataValidation);
    }

    /// <summary>
    /// Helper method for converting an object into a dynamic number.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    protected static dynamic AsNumber(object value)
    {
        if (value is null)
            return 0;

        var typeCode = Type.GetTypeCode(value.GetType());

        // sort out null, Object, DBNull since those cannot be casted to double
        if (typeCode < TypeCode.Boolean)
            return 0.0;

        if (typeCode == TypeCode.String)
            return Convert.ToDouble(value);

        return value;
    }

    private class Processor : IMultiValueConverter
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly MultiBinding _bindings = new MultiBinding { FallbackValue = null };
        private readonly Queue<Operation> _operations = new Queue<Operation>();
        private readonly IList<object> _data = new List<object>();

        public Processor(IServiceProvider serviceProvider)
        {
            _bindings.Converter = this;
            _serviceProvider = serviceProvider;
        }

        public InstancedBinding Execute(IAvaloniaObject target, AvaloniaProperty targetProperty, object anchor = null, bool enableDataValidation = false)
        {
            // empty MultiBindings will fail at runtime
            // simply add an unused Binding, which won't be used in evaluation,
            // since there is no matching operation
            if (_bindings.Bindings.Count == 0)
                _bindings.Bindings.Add(new Binding { Source = null });

            return _bindings.Initiate(target, targetProperty, anchor, enableDataValidation);
        }

        public void PushBinding(IBinding binding)
        {
            if (binding is MultiBinding multiBinding)
            {
                _operations.Enqueue(Operation.MultiBinding);
                _data.Add(multiBinding);

                foreach (var innerBinding in multiBinding.Bindings)
                    _bindings.Bindings.Add(innerBinding);
            }
            else
            {
                _operations.Enqueue(Operation.Binding);
                _bindings.Bindings.Add(binding);
            }
        }

        public void PushStaticObject(object staticObject)
        {
            _operations.Enqueue(Operation.Static);
            _data.Add(staticObject);
        }

        public void PushLogical(LogicalBase logical)
        {
            _operations.Enqueue(Operation.Logical);
            _data.Add(logical);
        }

        /// <InheritDoc />
        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
        {
            var bindingResults = new Queue<object>(values);
            var data = new Queue<object>(_data);

            var result = Execute(_operations, bindingResults, data, targetType);

            return AutoConvertValue(result, targetType);
        }

        private static object Execute(IEnumerable<Operation> operations, Queue<object> bindingResults, Queue<object> data, Type targetType)
        {
            var stack = new Stack<object>();

            foreach (var operation in operations)
            {
                switch (operation)
                {
                    case Operation.Binding:
                        stack.Push(bindingResults.Dequeue());
                        break;

                    case Operation.MultiBinding:
                        stack.Push(EvaluateMultiBinding(bindingResults, data, targetType));
                        break;

                    case Operation.Static:
                        stack.Push(data.Dequeue());
                        break;

                    case Operation.Logical:
                        stack.Push(EvaluateLogical(stack, data));
                        break;

                    default:
                        throw new InvalidOperationException();
                }
            }

            return stack.Single();
        }

        private static object EvaluateMultiBinding(Queue<object> bindingResults, Queue<object> data, Type targetType)
        {
            var multiBinding = (MultiBinding)data.Dequeue();
            var values = new object[multiBinding.Bindings.Count];

            for (var i = 0; i < multiBinding.Bindings.Count; i++)
                values[i] = bindingResults.Dequeue();

            if (multiBinding.Converter != null)
            {
                var value = multiBinding.Converter.Convert(values, targetType, multiBinding.ConverterParameter, CultureInfo.CurrentCulture);

                values = new[] { value };
            }

            if (multiBinding.StringFormat != null)
            {
                values = new[] { (object)string.Format(multiBinding.StringFormat, values) };
            }

            return values.Single();
        }

        private static object EvaluateLogical(Stack<object> stack, Queue<object> data)
        {
            var logical = (LogicalBase)data.Dequeue();

            var values = new List<object>();

            for (var i = 0; i < logical.Items.Count; i++)
                values.Insert(0, stack.Pop());

            return logical.Evaluate(values);
        }

        private static object AutoConvertValue(object value, Type targetType)
        {
            if (ReferenceEquals(value, AvaloniaProperty.UnsetValue))
            {
                if (targetType.IsValueType)
                    return Activator.CreateInstance(targetType);

                return null;
            }
            else if (targetType == typeof(string))
            {
                return value?.ToString();
            }
            else if (value != null && !targetType.IsInstanceOfType(value))
            {
                if (targetType.IsAssignableFrom(typeof(Brush)))
                {
                    if (value is Color colorValue)
                        return new SolidColorBrush(colorValue);
                }
                else if (typeof(IConvertible).IsAssignableFrom(targetType))
                {
                    try
                    {
                        return System.Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            return value;
        }

        private enum Operation
        {
            Binding,
            MultiBinding,
            Static,
            Logical
        }
    }
}

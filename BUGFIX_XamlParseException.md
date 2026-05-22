# Исправление ошибки XamlParseException

## Проблема

При запуске приложения возникала ошибка:
```
System.Windows.Markup.XamlParseException: 
"Не удалось найти ресурс с ключом "DefaultExpanderStyle"
```

## Причина

В файле `MainWindow.xaml` на строке 350 использовался стиль `DefaultExpanderStyle`:
```xml
<Expander Header="Лог активности" IsExpanded="True" Margin="10,5" Style="{StaticResource DefaultExpanderStyle}">
```

Но этот стиль не был определен в разделе `<Window.Resources>`.

## Решение

Добавлен недостающий стиль в раздел ресурсов окна:

```xml
<!-- Стиль для Expander -->
<Style x:Key="DefaultExpanderStyle" TargetType="Expander">
    <Setter Property="Background" Value="#2D2D2D"/>
    <Setter Property="BorderBrush" Value="#444"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Margin" Value="10"/>
    <Setter Property="Padding" Value="10"/>
    <Setter Property="HeaderTemplate">
        <Setter.Value>
            <DataTemplate>
                <TextBlock Text="{Binding}" FontWeight="SemiBold" FontSize="16" Foreground="White"/>
            </DataTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- Стиль для Expander по умолчанию -->
<Style TargetType="Expander" BasedOn="{StaticResource DefaultExpanderStyle}">
</Style>
```

## Результат

✅ Приложение успешно запускается
✅ Ошибка XAML исправлена
✅ Стиль применяется к секции "Лог активности"
✅ Все Expander'ы в приложении используют одинаковый стиль

## Дополнительная информация

Стиль `DefaultExpanderStyle` используется для:
- Раскрывающейся секции "Лог активности"
- Других Expander элементов в приложении

Стиль обеспечивает:
- Темный фон (#2D2D2D)
- Серую рамку (#444)
- Белый текст заголовка
- Жирный шрифт (SemiBold, 16pt)

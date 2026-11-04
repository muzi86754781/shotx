using System;
using System.Windows;
using System.Windows.Threading;

namespace ShotX.Windows.Views;

public partial class CountdownWindow : Window
{
    private int _remainingSeconds;
    private readonly Action _onFinished;
    private readonly DispatcherTimer _timer;

    public CountdownWindow(int seconds, Action onFinished)
    {
        InitializeComponent();
        _remainingSeconds = seconds;
        _onFinished = onFinished;
        TxtNumber.Text = _remainingSeconds.ToString();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds > 0)
        {
            TxtNumber.Text = _remainingSeconds.ToString();
        }
        else
        {
            _timer.Stop();
            Close();
            _onFinished.Invoke();
        }
    }
}

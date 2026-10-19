using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Origin.Core.Diagnostics;

namespace Origin.Infrastructure
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();
    }

    /// <summary>An async command that cannot be re-entered while it runs.</summary>
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _running;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => !_running && (_canExecute?.Invoke() ?? true);

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _running = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                // Commands report their own failures; this only stops an async void from crashing the app.
                Log.Error("Unhandled error in command", ex);
            }
            finally
            {
                _running = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}

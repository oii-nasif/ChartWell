using System.ComponentModel;
using HealthTracker.ViewModels;

namespace HealthTracker.Tests.ViewModels;

// Concrete subclass so we can test the protected methods
file class TestViewModel : BaseViewModel
{
    public void CallSetError(string msg) => SetError(msg);
    public void CallClearError() => ClearError();
}

public class BaseViewModelTests
{
    [Fact]
    public void IsBusy_WhenSet_RaisesPropertyChangedForIsBusyAndIsNotBusy()
    {
        var vm = new TestViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.IsBusy = true;

        Assert.Contains(nameof(BaseViewModel.IsBusy), raised);
        Assert.Contains(nameof(BaseViewModel.IsNotBusy), raised);
    }

    [Fact]
    public void IsNotBusy_IsInverseOfIsBusy()
    {
        var vm = new TestViewModel();
        Assert.True(vm.IsNotBusy);
        vm.IsBusy = true;
        Assert.False(vm.IsNotBusy);
    }

    [Fact]
    public void SetError_SetsHasErrorTrueAndMessage()
    {
        var vm = new TestViewModel();
        vm.CallSetError("Something went wrong");

        Assert.True(vm.HasError);
        Assert.Equal("Something went wrong", vm.ErrorMessage);
    }

    [Fact]
    public void ClearError_ResetsHasErrorAndMessage()
    {
        var vm = new TestViewModel();
        vm.CallSetError("error");
        vm.CallClearError();

        Assert.False(vm.HasError);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public void HasError_WhenSet_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BaseViewModel.HasError)) raised = true;
        };

        vm.HasError = true;
        Assert.True(raised);
    }

    [Fact]
    public void ErrorMessage_WhenSet_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BaseViewModel.ErrorMessage)) raised = true;
        };

        vm.ErrorMessage = "test";
        Assert.True(raised);
    }
}

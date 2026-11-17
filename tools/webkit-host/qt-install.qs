function Controller() {
    installer.autoRejectMessageBoxes();
    installer.setMessageBoxAutomaticAnswer("OverwriteTargetDirectory", QMessageBox.Yes);
    installer.setMessageBoxAutomaticAnswer("stopProcessesForUpdates", QMessageBox.Ignore);
    installer.setMessageBoxAutomaticAnswer("TargetDirectoryInUse", QMessageBox.No);
}

Controller.prototype.WelcomePageCallback = function() {
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.CredentialsPageCallback = function() {
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.IntroductionPageCallback = function() {
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.TargetDirectoryPageCallback = function() {
    gui.currentPageWidget().TargetDirectoryLineEdit.setText(installer.value("TargetDir"));
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.ComponentSelectionPageCallback = function() {
    var widget = gui.currentPageWidget();
    widget.deselectAll();
    widget.selectComponent("qt.55.win32_mingw492");
    widget.selectComponent("qt.tools.win32_mingw492");
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.LicenseAgreementPageCallback = function() {
    gui.currentPageWidget().AcceptLicenseRadioButton.setChecked(true);
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.StartMenuDirectoryPageCallback = function() {
    gui.clickButton(buttons.NextButton, 3000);
}

Controller.prototype.ReadyForInstallationPageCallback = function() {
    gui.clickButton(buttons.CommitButton, 3000);
}

Controller.prototype.PerformInstallationPageCallback = function() {
    gui.clickButton(buttons.CommitButton, 3000);
}

Controller.prototype.FinishedPageCallback = function() {
    var check = gui.currentPageWidget().LaunchQtCreatorCheckBoxForm;
    if (check) {
        check.launchQtCreatorCheckBox.setChecked(false);
    }
    gui.clickButton(buttons.FinishButton, 3000);
}

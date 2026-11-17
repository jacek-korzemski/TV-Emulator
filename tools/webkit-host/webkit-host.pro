QT += webkit webkitwidgets network
CONFIG += c++11 warn_on
CONFIG -= app_bundle
TEMPLATE = app
TARGET = tv-webkit
SOURCES += main.cpp

# GUI subsystem: no console window. The parent reads stdout from the inherited pipe.
win32: CONFIG -= console
win32: QMAKE_LFLAGS += -Wl,-subsystem,windows

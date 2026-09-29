package net.osa.osenginemobile;

import java.util.ArrayList;
import java.util.List;

final class VpsSnapshot {
    static final class Terminal {
        String name;
        String service;
        String state;
        long memoryBytes;
        double cpuPercent = Double.NaN;
    }

    final List<Terminal> terminals = new ArrayList<>();
    double cpuPercent = Double.NaN;
    double ramPercent = Double.NaN;
    double diskPercent = Double.NaN;
    long ramTotal;
    long ramUsed;
}

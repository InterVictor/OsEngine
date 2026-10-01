package net.osa.osenginemobile;

/** Historical test positions on VPS append " TestPaper" to the trading ticker. */
final class SecurityNames {
    private SecurityNames() { }

    static boolean sameTicker(String ticker, String positionSecurity) {
        return ticker != null && positionSecurity != null
            && (ticker.equals(positionSecurity)
                || (ticker + " TestPaper").equals(positionSecurity));
    }
}

package online.braumg.codexbridge;

import org.junit.Test;
import java.math.BigInteger;
import java.security.KeyPairGenerator;
import java.security.Signature;
import java.security.spec.ECGenParameterSpec;
import static org.junit.Assert.*;

public final class DeviceIdentityTest {
    @Test public void createsBase64UrlInstallationId() {
        String first = DeviceIdentity.createInstallationId();
        String second = DeviceIdentity.createInstallationId();
        assertTrue(first.matches("[A-Za-z0-9_-]{43}"));
        assertTrue(second.matches("[A-Za-z0-9_-]{43}"));
        assertNotEquals(first, second);
    }

    @Test public void convertsCanonicalDerToFixedP1363() throws Exception {
        var generator = KeyPairGenerator.getInstance("EC");
        generator.initialize(new ECGenParameterSpec("secp256r1"));
        var pair = generator.generateKeyPair();
        var signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(pair.getPrivate());
        signer.update("contract".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        byte[] converted = DeviceIdentity.derToP1363(signer.sign());
        assertEquals(64, converted.length);
        assertNotEquals(BigInteger.ZERO, new BigInteger(1, java.util.Arrays.copyOfRange(converted, 0, 32)));
        assertNotEquals(BigInteger.ZERO, new BigInteger(1, java.util.Arrays.copyOfRange(converted, 32, 64)));
    }

    @Test public void rejectsNonCanonicalAndTruncatedDer() {
        for (byte[] invalid : new byte[][] {
            {}, {0x30, 0x00}, {0x30, 0x06, 0x02, 0x01, 0x01, 0x02},
            {0x30, 0x06, 0x02, 0x02, 0x00, 0x01, 0x02, 0x00},
        }) assertThrows(IllegalArgumentException.class, () -> DeviceIdentity.derToP1363(invalid));
    }
}

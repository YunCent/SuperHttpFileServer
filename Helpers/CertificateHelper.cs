using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace SuperHttpFileServer
{
    /// <summary>
    /// 证书加载工具，使用 BouncyCastle 解析 PEM（支持 RSA/EC, PKCS#1/PKCS#8/SEC1 全格式）
    /// </summary>
    public static class CertificateHelper
    {
        public static X509Certificate2 LoadCertificate(string certPath, string keyPath)
        {
            if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
                throw new FileNotFoundException("证书文件不存在", certPath);

            byte[] certBytes = File.ReadAllBytes(certPath);

            // 1. 直接加载（PFX/P12 二进制，含私钥）
            try
            {
                var direct = new X509Certificate2(certBytes);
                if (direct.HasPrivateKey)
                    return EnsurePrivateKeyInStore(direct);
                direct.Dispose();
            }
            catch { }

            // 2. BouncyCastle PemReader 解析证书 + 私钥
            string certText = null;
            try { certText = System.Text.Encoding.UTF8.GetString(certBytes); } catch { }
            if (certText == null)
                throw new InvalidOperationException("证书文件不是有效的 PEM/DER 格式");

            // 2a. 用 BouncyCastle 读证书
            Org.BouncyCastle.X509.X509Certificate bcCert = null;
            AsymmetricKeyParameter privateKey = null;

            using (var reader = new StringReader(certText))
            {
                var pemReader = new PemReader(reader);
                object obj;
                while ((obj = pemReader.ReadObject()) != null)
                {
                    var cert = obj as Org.BouncyCastle.X509.X509Certificate;
                    if (cert != null && bcCert == null) { bcCert = cert; continue; }

                    var keyPair = obj as AsymmetricCipherKeyPair;
                    if (keyPair != null) { privateKey = keyPair.Private; continue; }

                    var keyParam = obj as AsymmetricKeyParameter;
                    if (keyParam != null && keyParam.IsPrivate) { privateKey = keyParam; continue; }
                }
            }

            // 从单独密钥文件加载私钥
            if (privateKey == null && !string.IsNullOrEmpty(keyPath) && keyPath != certPath && File.Exists(keyPath))
            {
                string keyText = File.ReadAllText(keyPath, System.Text.Encoding.UTF8);
                using (var reader = new StringReader(keyText))
                {
                    var pemReader = new PemReader(reader);
                    object obj;
                    while ((obj = pemReader.ReadObject()) != null)
                    {
                        var keyPair = obj as AsymmetricCipherKeyPair;
                        if (keyPair != null) { privateKey = keyPair.Private; break; }

                        var keyParam = obj as AsymmetricKeyParameter;
                        if (keyParam != null && keyParam.IsPrivate) { privateKey = keyParam; break; }
                    }
                }
            }

            if (bcCert == null)
                throw new InvalidOperationException("未找到有效证书");
            if (privateKey == null)
                throw new InvalidOperationException("未找到私钥，请确认密钥文件路径或格式");

            // 2b. 转换为 .NET 证书并关联私钥
            X509Certificate2 netCert = null;
            try
            {
                netCert = new X509Certificate2(bcCert.GetEncoded());
                var result = AttachPrivateKey(netCert, privateKey);
                // SslStream (SChannel) 要求私钥在 Windows 证书存储中才能使用
                // 将证书临时存入当前用户的 My 存储，导出再重新加载
                return EnsurePrivateKeyInStore(result);
            }
            finally
            {
                netCert?.Dispose();
            }
        }

        /// <summary>
        /// 确保证书私钥对 SChannel 可用：导出 PFX 后以 PersistKeySet 重新加载
        /// </summary>
        private static X509Certificate2 EnsurePrivateKeyInStore(X509Certificate2 cert)
        {
            try
            {
                // 导出为 PFX 字节
                byte[] pfx = cert.Export(X509ContentType.Pfx);
                cert.Dispose();

                // 以 PersistKeySet 重新加载，私钥会持久化到 CNG 密钥存储，SChannel 可访问
                var result = new X509Certificate2(pfx, (string)null,
                    X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                Array.Clear(pfx, 0, pfx.Length);
                return result;
            }
            catch
            {
                return cert;
            }
        }

        private static X509Certificate2 AttachPrivateKey(X509Certificate2 cert, AsymmetricKeyParameter privateKey)
        {
            string keyAlg = cert.GetKeyAlgorithm();

            if (keyAlg == "1.2.840.10045.2.1") // EC
                return AttachEcKey(cert, privateKey);
            else // RSA
                return AttachRsaKey(cert, privateKey);
        }

        private static X509Certificate2 AttachRsaKey(X509Certificate2 cert, AsymmetricKeyParameter privateKey)
        {
            var rsaPrivate = privateKey as RsaPrivateCrtKeyParameters;
            if (rsaPrivate == null)
                throw new InvalidOperationException("不支持的 RSA 私钥类型: " + privateKey.GetType().Name);

            RSAParameters rsaParams = DotNetUtilities.ToRSAParameters(rsaPrivate);
            using (var rsaObj = RSA.Create())
            {
                rsaObj.ImportParameters(rsaParams);
                var result = cert.CopyWithPrivateKey(rsaObj);
                if (result.HasPrivateKey) return result;
                result.Dispose();
                throw new InvalidOperationException("RSA 私钥关联失败");
            }
        }

        private static X509Certificate2 AttachEcKey(X509Certificate2 cert, AsymmetricKeyParameter privateKey)
        {
            var ecPrivate = privateKey as ECPrivateKeyParameters;
            if (ecPrivate == null)
                throw new InvalidOperationException("不支持的 EC 私钥类型: " + privateKey.GetType().Name);

            ECParameters ecParams = ToECParameters(ecPrivate);
            using (var ecdsa = new ECDsaCng())
            {
                ecdsa.ImportParameters(ecParams);
                var combined = cert.CopyWithPrivateKey(ecdsa);
                if (combined.HasPrivateKey) return combined;
                combined.Dispose();
                throw new InvalidOperationException("EC 私钥关联失败");
            }
        }

        private static ECParameters ToECParameters(ECPrivateKeyParameters ecPrivate)
        {
            var parameters = new ECParameters();
            ECDomainParameters domain = ecPrivate.Parameters;
            parameters.Curve = GetCurve(ecPrivate);
            parameters.D = ecPrivate.D.ToByteArrayUnsigned();

            // 公钥点 Q = G * D
            Org.BouncyCastle.Math.EC.ECPoint q = domain.G.Multiply(ecPrivate.D).Normalize();
            byte[] qEncoded = q.GetEncoded(false);
            int coordSize = (qEncoded.Length - 1) / 2;
            byte[] qx = new byte[coordSize];
            byte[] qy = new byte[coordSize];
            Array.Copy(qEncoded, 1, qx, 0, coordSize);
            Array.Copy(qEncoded, 1 + coordSize, qy, 0, coordSize);
            parameters.Q = new System.Security.Cryptography.ECPoint();
            parameters.Q.X = qx;
            parameters.Q.Y = qy;

            return parameters;
        }

        private static ECCurve GetCurve(ECPrivateKeyParameters ecPrivate)
        {
            string oidValue = ecPrivate.PublicKeyParamSet != null ? ecPrivate.PublicKeyParamSet.Id : null;

            if (oidValue == "1.2.840.10045.3.1.7") return ECCurve.NamedCurves.nistP256;  // P-256
            if (oidValue == "1.3.132.0.34") return ECCurve.NamedCurves.nistP384;          // P-384
            if (oidValue == "1.3.132.0.35") return ECCurve.NamedCurves.nistP521;          // P-521

            int fieldSize = ecPrivate.Parameters.Curve.FieldSize;
            if (fieldSize == 256) return ECCurve.NamedCurves.nistP256;
            if (fieldSize == 384) return ECCurve.NamedCurves.nistP384;
            if (fieldSize == 521) return ECCurve.NamedCurves.nistP521;

            throw new InvalidOperationException("不支持的 EC 曲线，OID=" + (oidValue ?? "null") + ", FieldSize=" + fieldSize);
        }
    }
}

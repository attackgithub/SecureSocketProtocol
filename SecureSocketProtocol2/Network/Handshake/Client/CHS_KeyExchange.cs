﻿using SecureSocketProtocol2.Encryptions;
using SecureSocketProtocol2.Hashers;
using SecureSocketProtocol2.Misc;
using SecureSocketProtocol2.Network.Handshake.Server;
using SecureSocketProtocol2.Network.Messages;
using SecureSocketProtocol2.Network.Messages.TCP;
using SecureSocketProtocol2.Network.Messages.TCP.Handshake;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SecureSocketProtocol2.Network.Handshake.Client
{
    internal class CHS_KeyExchange : Handshake
    {
        public CHS_KeyExchange(SSPClient client)
            : base(client)
        {

        }

        public override HandshakeType[] ServerTypes
        {
            get
            {
                return new HandshakeType[]
                {
                    HandshakeType.SendMessage,
                    HandshakeType.SendMessage,
                    HandshakeType.ReceiveMessage,
                };
            }
        }

        public override HandshakeType[] ClientTypes
        {
            get
            {
                return new HandshakeType[]
                {
                    HandshakeType.ReceiveMessage,
                    HandshakeType.ReceiveMessage,
                    HandshakeType.SendMessage,
                };
            }
        }

        public override bool onHandshake()
        {
            //wait for RSA from server
            RSAEncryption RSA = null;
            SyncObject syncObject = null;
            DiffieHellman diffieHellman = new DiffieHellman(256);

            if (!base.ReceiveMessage((IMessage message) =>
            {
                MsgRsaPublicKey rsaKey = message as MsgRsaPublicKey;

                if (rsaKey != null)
                {
                    RSA = new RSAEncryption(Connection.RSA_KEY_SIZE, rsaKey.Modulus, rsaKey.Exponent, true);
                    return true;
                }
                return false;
            }).Wait<bool>(false, 30000))
            {
                if (syncObject.TimedOut)
                    throw new TimeoutException(TimeOutMessage);
                Client.Disconnect(DisconnectReason.TimeOut);
                throw new Exception("The RSA Exchange failed");
            }

            //Calculate and apply the public key as key
            //If the key is spoofed the next packet that's being send could fail if public key is generated wrong :)
            byte[] SecretHash = SHS_KeyExchange.CalculateSecretHash(RSA.Parameters.Modulus, RSA.Parameters.Exponent);
            Client.Connection.protection.ApplyPrivateKey(SecretHash);

            bool BlockedCertificate = false;
            if (!(syncObject = base.ReceiveMessage((IMessage message) =>
            {
                MsgServerEncryption mse = message as MsgServerEncryption;

                if (mse != null)
                {
                    Client.UseUDP = mse.UseUdp;

                    uint[] TempKey = new uint[SecretHash.Length];
                    for (int i = 0; i < TempKey.Length; i++)
                        TempKey[i] = SecretHash[i];

                    byte[] EncryptedDiffieKey = new byte[mse.Key.Length];
                    Array.Copy(mse.Key, EncryptedDiffieKey, mse.Key.Length);

                    //Decrypt the diffie-hellman key with our SecretHash which is generated by our Public RSA
                    UnsafeXor XorEncryption = new UnsafeXor(TempKey, true);
                    XorEncryption.Decrypt(mse.Key, 0, mse.Key.Length);

                    //read the Diffie-Hellman key
                    long index = Client.PrivateKeyOffset % 65535;
                    if (index <= 4)
                        index = 10;

                    byte[] diffieData = new byte[BitConverter.ToInt32(mse.Key, (int)(index - 4))]; //get the key length
                    Array.Copy(mse.Key, index, diffieData, 0, diffieData.Length); //copy the diffie-hellman key in between random data

                    //fix RSA Encrypted Data
                    //Array.Copy(mse.Key, mse.Key.Length - (diffieLen.Length + diffieData.Length), mse.Key, index - 4, diffieLen.Length + diffieData.Length);
                    //Array.Resize(ref mse.Key, mse.Key.Length - (diffieLen.Length + diffieData.Length)); //set original size back

                    //check if key is original
                    uint KeyHash = BitConverter.ToUInt32(new CRC32().ComputeHash(mse.Key), 0);

                    if (KeyHash != mse.KeyHash)
                    {
                        return false;
                    }

                    diffieHellman.GenerateResponse(new PayloadReader(diffieData));
                    Client.Certificate = mse.certificate;

                    if (!Client.onVerifyCertificate(mse.certificate))
                    {
                        BlockedCertificate = true;
                        return false;
                    }

                    Client.Connection.protection.ApplyPrivateKey(EncryptedDiffieKey); //apply Encrypted Key
                    base.SendMessage(new MsgDiffiehellman(diffieHellman.GetDiffie()));
                    Client.Connection.protection.ApplyPrivateKey(diffieHellman.Key); //apply diffie-hellman key
                    return true;
                }
                return false;
            })).Wait<bool>(false, 30000))
            {
                Client.Disconnect(DisconnectReason.TimeOut);
                Client.onException(new Exception("Handshake went wrong, CHS_KeyExchange"), ErrorType.Core);
                if (!BlockedCertificate)
                {
                    if (syncObject.TimedOut)
                        throw new TimeoutException(TimeOutMessage);
                    throw new Exception("Diffie-Hellman key-exchange failed.");
                }
                throw new Exception("The certificate provided by the server was blocked by the user");
            }
            return true;
        }
    }
}

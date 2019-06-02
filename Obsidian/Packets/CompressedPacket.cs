﻿using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Org.BouncyCastle.Crypto;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Obsidian.Packets
{
    public class CompressedPacket : Packet
    {
        public int DataLength { get; private set; }

        public CompressedPacket(int packetid, byte[] packetdata) : base(packetid, packetdata) { }

        private CompressedPacket() { /* Only for the static method to _not_ error*/ }

        public override async Task WriteToStreamAsync(Stream stream, BufferedBlockCipher encrypt)//TODO
        {
            var packetLength = this.PacketId.GetVarintLength() + this._packetData.Length;
            // compress data
            var memstr = new MemoryStream();
            await memstr.WriteVarIntAsync(PacketId);
            await memstr.WriteAsync(this._packetData, 0, this._packetData.Length);

            var inflate = new InflaterInputStream(memstr);
            byte[] compdata = new byte[inflate.Length];
            await inflate.ReadAsync(compdata, 0, compdata.Length);

            await stream.WriteVarIntAsync(packetLength);
            await stream.WriteVarIntAsync(compdata.Length);
            await stream.WriteAsync(compdata, 0, compdata.Length);

            memstr.Dispose();
        }

        // shut the fuck up, I know what I'm doing
        #pragma warning disable CS0108 // Member hides inherited member; missing new keyword
        public static async Task<CompressedPacket> ReadFromStreamAsync(Stream stream)
        #pragma warning restore CS0108 // Member hides inherited member; missing new keyword
        {
            await Task.Yield();
            // read lengths
            var len = await stream.ReadVarIntAsync();
            var datalen = await stream.ReadVarIntAsync();

            // read compressed data
            var compdata = new byte[len - datalen.GetVarintLength()];
            await stream.ReadAsync(compdata, 0, len);

            // decompress data
            var data = new byte[datalen];
            var memstr = new MemoryStream(data);
            var deflate = new DeflaterOutputStream(memstr);
            await deflate.WriteAsync(compdata, 0, compdata.Length);
            memstr.Position = 0;

            var packetid = await memstr.ReadVarIntAsync();

            int arlen = 0;
            if (datalen - packetid.GetVarintLength() > -1)
                arlen = len - packetid.GetVarintLength();

            var thedata = new byte[arlen];
            await memstr.ReadAsync(thedata, 0, thedata.Length);

            memstr.Dispose();
            deflate.Dispose();

            return new CompressedPacket()
            {
                PacketId = packetid,
                DataLength = datalen
            };
        }

        #region ignore for now.
        protected override Task PopulateAsync()
        {
            throw new NotImplementedException();
        }

        public override Task<byte[]> ToArrayAsync()
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}

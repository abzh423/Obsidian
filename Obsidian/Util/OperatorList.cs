﻿using Newtonsoft.Json;
using Obsidian.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Obsidian.Util
{
    public class OperatorList
    {
        private List<Operator> _ops;
        private List<OperatorRequest> _reqs;
        private Server _server;

        public OperatorList(Server s)
        {
            _ops = new List<Operator>();
            _reqs = new List<OperatorRequest>();
            _server = s;
        }

        public void Initialize()
        {
            if (!File.Exists("ops.json"))
            {
                using (var opfile = File.CreateText("ops.json"))
                {
                    opfile.Write(JsonConvert.SerializeObject(_ops));
                }
            }
            else
            {
                _ops = JsonConvert.DeserializeObject<List<Operator>>(File.ReadAllText("ops.json"));
            }
        }

        public void AddOperator(Player p)
        {
            _ops.Add(new Operator() { Username = p.Username, UUID = _server.Config.OnlineMode ? p?.UUID : null });
            _updateList();
        }

        public bool CreateRequest(Player p)
        {
            if (!_server.Config.AllowOperatorRequests)
            {
                return false;
            }

            var result = !_reqs.Any(r => r.Player == p);

            if (result)
            {
                var req = new OperatorRequest(p);

                _server.Logger.LogWarningAsync("New operator request from {p.Username}: " + req.Code);

                _reqs.Add(req);
            }

            return result;
        }

        public bool ProcessRequest(Player p, string code)
        {
            var result = _reqs.FirstOrDefault(r => r.Player == p && r.Code == code);

            if (result == null)
            {
                return false;
            }

            _reqs.Remove(result);

            AddOperator(p);

            return true;
        }

        public void AddOperator(string username)
        {
            _ops.Add(new Operator() { Username = username, UUID = null });
            _updateList();
        }

        public void RemoveOperator(Player p)
        {
            _ops.RemoveAll(x => x.UUID == p.UUID || x.Username == p.Username);
            _updateList();
        }

        public void RemoveOperator(string username)
        {
            _ops.RemoveAll(x => x.Username == username);
            _updateList();
        }

        public void RemoveOperator(Guid uuid)
        {
            _ops.RemoveAll(x => x.UUID == uuid);
            _updateList();
        }

        public bool IsOperator(Player p)
        {
            return _ops.Any(x =>
                (x.Username == p.Username || p.UUID == x.UUID)
                 && x.Online == _server.Config.OnlineMode
                 );
        }

        private void _updateList()
        {
            File.WriteAllText("ops.json", JsonConvert.SerializeObject(_ops));
        }

        // we only use this in this class
        private class Operator
        {
            [JsonProperty("username")]
            public string Username;

            [JsonProperty("uuid")]
            public Guid? UUID;

            [JsonIgnore]
            public bool Online => UUID != null;
        }

        private class OperatorRequest
        {
            public Player Player;
            public string Code;

            public OperatorRequest(Player player)
            {
                Player = player ?? throw new ArgumentNullException(nameof(player));

                string GetCode()
                {
                    string code = "";
                    var random = new Random();
                    const string chars = "0123456789ABCDEF";
                    for (int i = 0; i < 10; i++)
                    {
                        code += chars[random.Next(0, chars.Length - 1)];
                    }

                    return code;
                }

                Code = GetCode();
            }
        }
    }
}
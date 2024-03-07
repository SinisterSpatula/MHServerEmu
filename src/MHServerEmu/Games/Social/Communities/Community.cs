﻿using System.Text;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Common.Extensions;
using MHServerEmu.Common.Logging;
using MHServerEmu.Games.Entities;

namespace MHServerEmu.Games.Social.Communities
{
    /// <summary>
    /// Contains all players displayed in the social tab sorted by circles..
    /// </summary>
    public class Community
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly Dictionary<ulong, CommunityMember> _communityMemberDict = new();   // key is DbId

        private int _numCircleIteratorsInScope = 0;
        private int _numMemberIteratorsInScope = 0;

        public Player Owner { get; }

        public CommunityCircleManager CircleManager { get; }
        public int NumCircles { get => CircleManager.NumCircles; }
        public int NumMembers { get => _communityMemberDict.Count; }

        public Community(Player owner)
        {
            Owner = owner;
            CircleManager = new(this);
        }

        /// <summary>
        /// Initializes this <see cref="Community"/> instance.
        /// </summary>
        public bool Initialize()
        {
            return CircleManager.Initialize();
        }

        /// <summary>
        /// Clears this <see cref="Community"/> instance.
        /// </summary>
        public void Shutdown()
        {
            CircleManager.Shutdown();
            _communityMemberDict.Clear();
        }

        public bool Decode(CodedInputStream stream)
        {
            CircleManager.Decode(stream);

            int numCommunityMembers = stream.ReadRawInt32();
            for (int i = 0; i < numCommunityMembers; i++)
            {
                string playerName = stream.ReadRawString();
                ulong playerDbId = stream.ReadRawVarint64();

                // Get an existing member to deserialize into
                CommunityMember member = GetMember(playerDbId);

                // If not found create a new member
                if (member == null)
                {
                    member = CreateMember(playerDbId, playerName);
                    if (member == null) return false;   // Bail out if member creation failed
                }

                // Deserialize data into our member
                member.Decode(stream);

                // Get rid of members that don't have any circles for some reason
                if (member.NumCircles() == 0)
                    DestroyMember(member);
            }

            return true;
        }

        public void Encode(CodedOutputStream stream)
        {
            CircleManager.Encode(stream);

            stream.WriteRawInt32(_communityMemberDict.Count);
            foreach (CommunityMember member in _communityMemberDict.Values)
            {
                stream.WriteRawString(member.GetName());
                stream.WriteRawVarint64(member.DbId);
                member.Encode(stream);
            }
        }

        /// <summary>
        /// Returns the <see cref="CommunityMember"/> with the specified dbId. Returns <see langword="null"/> if not found.
        /// </summary>
        public CommunityMember GetMember(ulong dbId)
        {
            if (_communityMemberDict.TryGetValue(dbId, out CommunityMember member) == false)
                return null;

            return member;
        }

        /// <summary>
        /// Returns the <see cref="CommunityMember"/> with the specified name. Returns <see langword="null"/> if not found.
        /// </summary>
        public CommunityMember GetMemberByName(string playerName)
        {
            foreach (CommunityMember member in IterateMembers())
            {
                if (member.GetName() == playerName)
                    return member;
            }

            return null;
        }

        /// <summary>
        /// Adds a new <see cref="CommunityMember"/> to the specified circle. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool AddMember(ulong playerDbId, string playerName, CircleId circleId)
        {
            // Get an existing member to add to the circle
            CommunityMember member = GetMember(playerDbId);

            // If not found create a new member
            if (member == null)
            {
                member = CreateMember(playerDbId, playerName);
                if (member == null)     // Bail out if member creation failed
                    return Logger.WarnReturn(false, $"AddMember(): Failed to get or create a member for dbId {playerDbId}");
            }

            // Get the circle
            CommunityCircle circle = GetCircle(circleId);
            if (circle == null)
                return Logger.WarnReturn(false, $"AddMember(): Failed to get circle for circleId {circleId}");

            return circle.AddMember(member);
        }

        /// <summary>
        /// Removes the specified <see cref="CommunityMember"/> from the specified circle. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RemoveMember(ulong playerDbId, CircleId circleId)
        {
            CommunityMember member = GetMember(playerDbId);
            if (member == null)
                return Logger.WarnReturn(false, $"RemoveMember(): Failed to get member for dbId {playerDbId}");

            CommunityCircle circle = GetCircle(circleId);
            if (circle == null)
                return Logger.WarnReturn(false, $"RemoveMember(): Failed to get circle for cicleId {circleId}");

            bool wasRemoved = circle.RemoveMember(member);
            
            // Remove the member from this community once it's no longer part of any circles
            if (member.NumCircles() == 0)
                DestroyMember(member);

            return wasRemoved;
        }

        /// <summary>
        /// Returns the number <see cref="CommunityMember"/> instances belonging to the specified <see cref="CircleId"/>.
        /// </summary>
        public int NumMembersInCircle(CircleId circleId)
        {
            CommunityCircle circle = GetCircle(circleId);
            if (circle == null)
                return Logger.WarnReturn(0, $"NumMembersInCircle(): circle == null");

            int numMembers = 0;
            foreach (CommunityMember member in IterateMembers())
            {
                if (member.IsInCircle(circle))
                    numMembers++;
            }
            return numMembers;
        }

        /// <summary>
        /// Receives a <see cref="CommunityMemberBroadcast"/> and routes it to the relevant <see cref="CommunityMember"/>.
        /// </summary>
        public bool ReceiveMemberBroadcast(CommunityMemberBroadcast broadcast)
        {
            ulong playerDbId = broadcast.MemberPlayerDbId;
            if (playerDbId == 0)
                return Logger.WarnReturn(false, $"ReceiveMemberBroadcast(): Invalid playerDbId");

            CommunityMember member = GetMember(playerDbId);
            if (member == null)
                return Logger.WarnReturn(false, $"ReceiveMemberBroadcast(): PlayerDbId {playerDbId} not found");

            member.ReceiveBroadcast(broadcast);
            return true;
        }

        public override string ToString()
        {
            StringBuilder sb = new();

            sb.AppendLine($"{nameof(CircleManager)}: {CircleManager}");

            foreach (var kvp in _communityMemberDict)
                sb.AppendLine($"Member[{kvp.Key}]: {kvp.Value}");                

            return sb.ToString();
        }

        /// <summary>
        /// Returns the <see cref="CommunityCircle"/> of this <see cref="Community/> with the specified id.
        /// </summary>
        public CommunityCircle GetCircle(CircleId circleId) => CircleManager.GetCircle(circleId);

        /// <summary>
        /// Returns the name of the specified <see cref="CircleId"/>.
        /// </summary>
        public static string GetLocalizedSystemCircleName(CircleId id)
        {
            // NOTE: This is overriden in CCommunity to return the actually localized string.
            // Base implementation just returns the string representation of the value.
            // This string is later serialized to the client and used to look up the id.
            return id.ToString();
        }

        #region Iterators

        // These methods are replacements for CommunityCircleIterator and CommunityMemberIterator classes

        /// <summary>
        /// Iterates all <see cref="CommunityCircle"/> instances in this <see cref="Community"/>.
        /// </summary>
        public IEnumerable<CommunityCircle> IterateCircles()
        {
            _numCircleIteratorsInScope++;

            try
            {
                foreach (CommunityCircle circle in CircleManager)
                    yield return circle;
            }
            finally
            {
                _numCircleIteratorsInScope--;
            }
        }

        /// <summary>
        /// Iterates all <see cref="CommunityCircle"/> instances that the provided <see cref="CommunityMember"/> belongs to.
        /// </summary>
        public IEnumerable<CommunityCircle> IterateCircles(CommunityMember member)
        {
            _numCircleIteratorsInScope++;

            try
            {
                foreach (CommunityCircle circle in CircleManager)
                {
                    if (member.IsInCircle(circle))
                        yield return circle;
                }
            }
            finally
            {
                _numCircleIteratorsInScope--;
            }
        }

        /// <summary>
        /// Iterates all <see cref="CommunityMember"/> instances in this <see cref="Community"/>
        /// </summary>
        public IEnumerable<CommunityMember> IterateMembers()
        {
            _numMemberIteratorsInScope++;

            try
            {
                foreach (CommunityMember member in _communityMemberDict.Values)
                    yield return member;
            }
            finally
            {
                _numMemberIteratorsInScope--;
            }
        }

        /// <summary>
        /// Iterates all <see cref="CommunityMember"/> instances in this <see cref="Community"/>.
        /// </summary>
        public IEnumerable<CommunityMember> IterateMembers(CommunityCircle circle)
        {
            _numMemberIteratorsInScope++;

            try
            {
                foreach (CommunityMember member in _communityMemberDict.Values)
                {
                    if (member.IsInCircle(circle))
                        yield return member;
                }
            }
            finally
            {
                _numMemberIteratorsInScope--;
            }
        }

        #endregion

        /// <summary>
        /// Creates a new <see cref="CommunityMember"/> instance for the specified DbId for this <see cref="Community"/>.
        /// </summary>
        private CommunityMember CreateMember(ulong playerDbId, string playerName)
        {
            if (_numMemberIteratorsInScope > 0)
                return Logger.WarnReturn<CommunityMember>(null, $"CreateMember(): Trying to create a new member while iterating the community {this}");

            if (playerDbId == 0)
                return Logger.WarnReturn<CommunityMember>(null, $"CreateMember(): Invalid player id when creating community member {playerName}");

            CommunityMember existingMember = GetMember(playerDbId);
            if (existingMember != null)
                return Logger.WarnReturn<CommunityMember>(null, $"CreateMember(): Member already exists {existingMember}");

            CommunityMember newMember = new(this, playerDbId, playerName);
            _communityMemberDict.Add(playerDbId, newMember);
            return newMember;
        }

        /// <summary>
        /// Removes the provided <see cref="CommunityMember"/> instance from this <see cref="Community"/>.
        /// </summary>
        private bool DestroyMember(CommunityMember member)
        {
            if (_numMemberIteratorsInScope > 0)
                return Logger.WarnReturn(false, $"CreateMember(): Trying to destroy a member while iterating the community");

            return _communityMemberDict.Remove(member.DbId);
        }
    }
}

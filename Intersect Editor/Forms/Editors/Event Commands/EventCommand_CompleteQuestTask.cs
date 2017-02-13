﻿/*
    Intersect Game Engine (Editor)
    Copyright (C) 2015  JC Snider, Joe Bridges
    
    Website: http://ascensiongamedev.com
    Contact Email: admin@ascensiongamedev.com 

    This program is free software; you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation; either version 2 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License along
    with this program; if not, write to the Free Software Foundation, Inc.,
    51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA.
*/
using System;
using System.Windows.Forms;
using Intersect_Editor.Classes;
using Intersect_Editor.Classes.Core;
using Intersect_Library;
using Intersect_Library.GameObjects;
using Intersect_Library.GameObjects.Events;
using Intersect_Library.Localization;

namespace Intersect_Editor.Forms.Editors.Event_Commands
{
    public partial class EventCommand_CompleteQuestTask : UserControl
    {
        private EventCommand _myCommand;
        private readonly FrmEvent _eventEditor;
        public EventCommand_CompleteQuestTask(EventCommand refCommand, FrmEvent editor)
        {
            InitializeComponent();
            _myCommand = refCommand;
            _eventEditor = editor;
            InitLocalization();
            cmbQuests.Items.Clear();
            cmbQuests.Items.AddRange(Database.GetGameObjectList(GameObject.Quest));
            cmbQuests.SelectedIndex = Database.GameObjectListIndex(GameObject.Quest, refCommand.Ints[0]);
        }

        private void InitLocalization()
        {
            grpCompleteTask.Text = Strings.Get("eventcompletequesttask", "title");
            lblQuest.Text = Strings.Get("eventcompletequesttask", "quest");
            lblTask.Text = Strings.Get("eventcompletequesttask", "task");
            btnSave.Text = Strings.Get("eventcompletequesttask", "okay");
            btnCancel.Text = Strings.Get("eventcompletequesttask", "cancel");
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            _myCommand.Ints[0] = Database.GameObjectIdFromList(GameObject.Quest, cmbQuests.SelectedIndex);
            _myCommand.Ints[1] = -1;
            if (cmbQuests.SelectedIndex > -1)
            {
                var quest = QuestBase.GetQuest(Database.GameObjectIdFromList(GameObject.Quest, cmbQuests.SelectedIndex));
                if (quest != null)
                {
                    var i = -1;
                    foreach (var task in quest.Tasks)
                    {
                        i++;
                        if (i == cmbQuestTask.SelectedIndex)
                        {
                            _myCommand.Ints[1] = task.Id;
                        }
                    }
                }
            }
            _eventEditor.FinishCommandEdit();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            _eventEditor.CancelCommandEdit();
        }

        private void cmbQuests_SelectedIndexChanged(object sender, EventArgs e)
        {
            cmbQuestTask.Hide();
            lblTask.Hide();
            if (cmbQuests.SelectedIndex > -1)
            {
                var quest = QuestBase.GetQuest(Database.GameObjectIdFromList(GameObject.Quest, cmbQuests.SelectedIndex));
                if (quest != null)
                {
                    lblTask.Show();
                    cmbQuestTask.Show();
                    cmbQuestTask.Items.Clear();
                    foreach (var task in quest.Tasks)
                    {
                        cmbQuestTask.Items.Add(task.GetTaskString());
                        if (task.Id == _myCommand.Ints[1])
                        {
                            cmbQuestTask.SelectedIndex = cmbQuestTask.Items.Count - 1;
                        }
                    }
                }
            }
        }
    }
}